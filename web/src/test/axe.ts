import axe from 'axe-core'

/**
 * The one WCAG policy both automated layers share (docs/ACCESSIBILITY.md).
 *
 * Component layer (this helper, jsdom): every page-level test file and the
 * major composites call `expectNoAxeViolations()` after rendering. It runs
 * axe-core directly rather than the vitest-axe matcher — vitest-axe 0.1.0's
 * `toHaveNoViolations` type augmentation targets the pre-Vitest-4 assertion
 * interface, so the matcher registered but didn't type-check. A plain helper
 * fixes that at the root (no matcher, no module augmentation to chase across
 * Vitest majors), gives one canonical place for the tag set and the jsdom
 * exclusions, and prints the offending rules/nodes on failure instead of an
 * opaque diff.
 *
 * Browser layer (web/a11y, Playwright + real Chromium): re-runs the same tag
 * set over the rendered capture set WITHOUT the jsdom exclusions — that layer
 * owns everything excluded here.
 */
export const WCAG_22_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

/**
 * Rules in the WCAG 2.2 AA tag set that cannot produce trustworthy results
 * in jsdom, each with the reason. Excluded HERE ONLY — the Playwright layer
 * runs them against real rendering. Keep this table in sync with
 * docs/ACCESSIBILITY.md.
 */
export const JSDOM_EXCLUDED_RULES: Readonly<Record<string, string>> = {
  // 1.4.3 Contrast (Minimum): contrast needs painted pixels — computed
  // colors, inheritance through layers, and background images. jsdom does
  // not lay out or paint, so axe would return "incomplete"/wrong answers.
  'color-contrast': 'WCAG 1.4.3 — requires real rendering; jsdom does not paint',
  // 2.5.8 Target Size (Minimum): measures bounding boxes; every jsdom rect
  // is 0×0, so the rule cannot distinguish a 48px button from a 10px one.
  'target-size': 'WCAG 2.5.8 — requires layout; jsdom rects are all 0×0',
  // 1.4.1 Use of Color: this rule detects links distinguishable only by
  // color, which requires computed text/link colors jsdom does not resolve.
  'link-in-text-block': 'WCAG 1.4.1 — requires computed colors; jsdom does not style',
}

/**
 * Asserts the subtree (default: the whole test document body, so portaled
 * menus/dialogs/popovers are included) has no axe violations under the
 * WCAG 2.2 AA tag set, minus the documented jsdom exclusions above.
 *
 * axe-core cannot run twice concurrently in one document — await each call.
 */
export async function expectNoAxeViolations(node: Element | Document = document.body): Promise<void> {
  const results = await axe.run(node as axe.ElementContext, {
    runOnly: { type: 'tag', values: [...WCAG_22_AA_TAGS] },
    rules: Object.fromEntries(Object.keys(JSDOM_EXCLUDED_RULES).map((id) => [id, { enabled: false }])),
  })
  if (results.violations.length > 0) {
    const detail = results.violations
      .map((v) => {
        const nodes = v.nodes
          .slice(0, 5)
          .map((n) => `    ${n.target.join(' ')}\n      ${n.failureSummary?.replace(/\n/g, '\n      ') ?? ''}`)
          .join('\n')
        const more = v.nodes.length > 5 ? `\n    …and ${v.nodes.length - 5} more node(s)` : ''
        return `  ${v.id} [${v.impact ?? 'unknown'}] ${v.help}\n  ${v.helpUrl}\n${nodes}${more}`
      })
      .join('\n\n')
    throw new Error(`Expected no axe violations, found ${results.violations.length} rule(s) violated:\n\n${detail}`)
  }
}
