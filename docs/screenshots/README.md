# README screenshots

These are **real components with staged sample data**, not a live
deployment: the capture harness renders the actual SPA (the same code and
stylesheets that ship) through the test suite's mock seams, and a headless
Chromium screenshots the result. The pixels are genuine; the data is
staged; nothing here proves a running system (design.md §16's standing
caveat applies).

To regenerate after UI changes:

```bash
cd web
npm ci && npm run codegen
PREVIEW_OUT=/some/tmp/dir npx vitest run src/preview/captureScreens.test.tsx
# then screenshot each emitted HTML file with any browser at 1440x900, e.g.:
#   npx playwright screenshot --viewport-size=1440,900 file:///.../page-view.html page-view.png
```

Without `PREVIEW_OUT` the capture test still runs (as a smoke test that the
composed screens mount) and writes nothing.
