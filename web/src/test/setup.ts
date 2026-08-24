import '@testing-library/jest-dom/vitest'
// Accessibility assertions: no matcher registration here. vitest-axe's
// `toHaveNoViolations` augmentation targeted the pre-Vitest-4 assertion
// interface (registered but untyped), so it was replaced with a plain
// helper — import { expectNoAxeViolations } from '../test/axe' — which
// also centralizes the WCAG 2.2 AA tag set and the documented jsdom rule
// exclusions in one place. See docs/ACCESSIBILITY.md.
import { afterEach } from 'vitest'
import { cleanup } from '@testing-library/react'

// `@testing-library/react`'s auto-cleanup only self-registers when it finds
// a *global* `afterEach` (Jest-style). This project runs Vitest with
// `globals: false` (test files import `describe`/`it`/`expect` explicitly
// rather than relying on injected globals), so that auto-detection never
// fires and the DOM silently accumulates across tests in the same file.
// Registering it explicitly here works regardless of the `globals` setting.
afterEach(() => {
  cleanup()
})
