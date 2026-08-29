import { readdirSync, existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { pathToFileURL, fileURLToPath } from 'node:url'
import { test, expect } from '@playwright/test'
import { AxeBuilder } from '@axe-core/playwright'

/**
 * One test per captured screen (both themes): load the standalone HTML in
 * real Chromium and run axe with the full WCAG 2.2 AA tag set — no rule
 * exclusions in this layer. This is where color-contrast (1.4.3) and
 * target-size (2.5.8) are actually enforced; the jsdom layer documents them
 * as excluded and defers here (web/src/test/axe.ts).
 *
 * Screens come from `npm run codegen && PREVIEW_OUT=web/a11y/screens
 * npx vitest run src/preview/a11yScreens.test.tsx` in web/ — see
 * docs/ACCESSIBILITY.md ("Running the checks locally").
 */

const WCAG_22_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

const here = dirname(fileURLToPath(import.meta.url))
const screensDir = process.env.A11Y_SCREENS_DIR ?? join(here, 'screens')
const reportDir = join(here, 'report')

const screens = existsSync(screensDir) ? readdirSync(screensDir).filter((f) => f.endsWith('.html')) : []

test('capture set exists and is complete', () => {
  // Fail loudly if the generation step was skipped or silently produced
  // nothing — a green run over zero files would be a tier that doesn't exist.
  // 23 screens × 2 themes; update alongside a11yScreens.test.tsx. The generator
  // now clears this directory before writing, so the count is what a CLEAN
  // checkout produces. It previously was not: page-properties was replaced by
  // page-details, nothing deleted its two files, and every local run kept
  // reporting 48 while CI — which starts empty — produced 46. The number looked
  // verified on the one machine that could never reproduce CI.
  expect(screens.length, `no .html captures found in ${screensDir}`).toBeGreaterThanOrEqual(46)
  const stems = new Set(screens.map((f) => f.replace(/--(light|dark)\.html$/, '')))
  for (const stem of stems) {
    expect(screens, `${stem} is missing a theme variant`).toContain(`${stem}--light.html`)
    expect(screens, `${stem} is missing a theme variant`).toContain(`${stem}--dark.html`)
  }
})

for (const file of screens) {
  test(`axe WCAG 2.2 AA: ${file}`, async ({ page }, testInfo) => {
    // The `os-dark` project exists for ONE combination: an app rendering in
    // light mode inside a browser whose `prefers-color-scheme` is dark. The
    // plain-CSS layer (editor-content.css) themes on both the media query and
    // `data-theme`, so before its light reset existed that pair produced light
    // chrome around a dark-variabled page body — a real 1.4.3 failure that no
    // capture could show, because captures stamp `data-theme` and Chromium
    // reports the media query as light unless told otherwise. Running the DARK
    // captures under a dark OS would just re-test agreement; the light ones are
    // the disagreement.
    if (testInfo.project.name === 'os-dark' && !file.endsWith('--light.html')) {
      test.skip()
    }
    await page.goto(pathToFileURL(join(screensDir, file)).href)
    // Fonts/layout settle — axe reads computed geometry for target-size.
    await page.waitForLoadState('load')

    const results = await new AxeBuilder({ page }).withTags(WCAG_22_AA_TAGS).analyze()

    if (results.violations.length > 0) {
      mkdirSync(reportDir, { recursive: true })
      writeFileSync(
        join(reportDir, `${file.replace(/\.html$/, '')}.violations.json`),
        JSON.stringify(results.violations, null, 2),
      )
    }

    const summary = results.violations
      .map(
        (v) =>
          `${v.id} [${v.impact}] ${v.help}\n` +
          v.nodes
            .slice(0, 10)
            .map((n) => `  ${n.target.join(' ')}\n    ${n.failureSummary?.replace(/\n/g, '\n    ')}`)
            .join('\n'),
      )
      .join('\n\n')

    expect(summary, `axe violations in ${file}`).toBe('')
  })
}
