import { defineConfig, devices } from '@playwright/test'

/**
 * Browser-layer accessibility runner (docs/ACCESSIBILITY.md): loads the
 * self-contained HTML capture set produced by
 * web/src/preview/a11yScreens.test.tsx (PREVIEW_OUT-gated) and runs
 * axe-core with the FULL WCAG 2.2 AA tag set — including the rules the
 * jsdom layer must exclude (color-contrast, target-size), because here
 * there is a real layout and real painted pixels.
 *
 * Chromium only, deliberately: axe results are engine-independent enough
 * that one real browser is the right cost/coverage trade for CI; the
 * captures are static HTML, so there is no cross-browser behavior to probe.
 */
export default defineConfig({
  testDir: '.',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: [
    ['list'],
    ['html', { outputFolder: 'report/html', open: 'never' }],
    ['json', { outputFile: 'report/results.json' }],
  ],
  use: {
    ...devices['Desktop Chrome'],
  },
  projects: [{ name: 'chromium' }],
})
