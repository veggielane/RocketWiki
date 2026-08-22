import '@testing-library/jest-dom/vitest'
// Type augmentation only (adds `.toHaveNoViolations()` to Vitest's Assertion
// type) — the runtime matcher is registered explicitly below, per
// vitest-axe's own docs, since this file is imported for its side effect on
// types rather than to get the matcher itself.
import 'vitest-axe/extend-expect'
import * as axeMatchers from 'vitest-axe/matchers'
import { afterEach, expect } from 'vitest'
import { cleanup } from '@testing-library/react'

expect.extend(axeMatchers)

// `@testing-library/react`'s auto-cleanup only self-registers when it finds
// a *global* `afterEach` (Jest-style). This project runs Vitest with
// `globals: false` (test files import `describe`/`it`/`expect` explicitly
// rather than relying on injected globals), so that auto-detection never
// fires and the DOM silently accumulates across tests in the same file.
// Registering it explicitly here works regardless of the `globals` setting.
afterEach(() => {
  cleanup()
})
