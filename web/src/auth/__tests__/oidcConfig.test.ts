import { describe, expect, it } from 'vitest'
import { InMemoryWebStorage } from 'oidc-client-ts'
import { oidcConfig } from '../oidcConfig'

/**
 * The two OIDC stores must NOT share a backing store, and the reason is not
 * stylistic.
 *
 * Sign-in state (the PKCE `code_verifier`, `state` and `nonce`) is written
 * before the browser navigates away to Keycloak and read back after it
 * navigates home. A full page navigation destroys anything held in memory, so
 * an in-memory state store makes the code exchange fail every time: Keycloak
 * returns a valid `code`, oidc-client-ts finds no matching state, and the SPA
 * sits on "Completing sign-in…" forever. That is exactly what happened on the
 * first real login this project ever performed, and no mocked test could see
 * it, because mocks never reload the page.
 *
 * The user store is the opposite requirement — design.md §11 keeps tokens in
 * memory so they cannot outlive the tab or be read out of web storage by an
 * XSS payload. Hence: different stores, on purpose.
 */
// AuthProviderProps is a union (settings-or-UserManager), so the settings half
// has to be named explicitly before these fields are reachable.
const settings = oidcConfig as unknown as {
  userStore: { _store: Storage }
  stateStore: { _store: Storage }
  scope: string
  response_type: string
}

describe('oidcConfig storage', () => {
  it('keeps tokens in memory only (design.md §11)', () => {
    expect(settings.userStore._store).toBeInstanceOf(InMemoryWebStorage)
  })

  it('keeps sign-in state somewhere that survives the redirect to Keycloak', () => {
    expect(settings.stateStore._store).not.toBeInstanceOf(InMemoryWebStorage)
    expect(settings.stateStore._store).toBe(window.sessionStorage)
  })

  it('scopes that state to the tab — sessionStorage, never localStorage', () => {
    expect(settings.stateStore._store).not.toBe(window.localStorage)
  })

  it('requests the authorization-code flow the realm is configured for', () => {
    expect(settings.scope).toContain('openid')
    expect(settings.response_type).toBe('code')
  })
})
