import { describe, expect, it } from 'vitest'
import { InMemoryWebStorage } from 'oidc-client-ts'
import { readOidcConfig, type OidcEnv } from '../oidcConfig'

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
 *
 * Built from an EXPLICIT env rather than the module's own `import.meta.env`
 * singleton. Vite loads `.env.local`, which is gitignored — so a test reading
 * the singleton passes on the machine that has one and fails in CI, which is
 * precisely the class of bug the fail-closed change below is about.
 */
const ORIGIN = 'https://wiki.example.internal'
const ENV: OidcEnv = { VITE_OIDC_AUTHORITY: 'https://keycloak.example.internal/realms/rocketwiki' }

/** AuthProviderProps is a union (settings-or-UserManager), so the settings half has to be named. */
function settingsFor(env: OidcEnv = ENV) {
  const config = readOidcConfig(env, ORIGIN)
  if (config === null) throw new Error('expected a configuration')
  return config as unknown as {
    userStore: { _store: Storage }
    stateStore: { _store: Storage }
    scope: string
    response_type: string
    authority: string
    client_id: string
    redirect_uri: string
    post_logout_redirect_uri: string
  }
}

describe('oidcConfig storage', () => {
  it('keeps tokens in memory only (design.md §11)', () => {
    expect(settingsFor().userStore._store).toBeInstanceOf(InMemoryWebStorage)
  })

  it('keeps sign-in state somewhere that survives the redirect to Keycloak', () => {
    expect(settingsFor().stateStore._store).not.toBeInstanceOf(InMemoryWebStorage)
    expect(settingsFor().stateStore._store).toBe(window.sessionStorage)
  })

  it('scopes that state to the tab — sessionStorage, never localStorage', () => {
    expect(settingsFor().stateStore._store).not.toBe(window.localStorage)
  })

  it('requests the authorization-code flow the realm is configured for', () => {
    expect(settingsFor().scope).toContain('openid')
    expect(settingsFor().response_type).toBe('code')
  })
})

/**
 * The authority is the one auth-critical value that cannot have a default.
 *
 * It used to fall back to `http://localhost:8080/realms/rocketwiki`. An image
 * built without the build-arg looked entirely healthy, and the first anyone
 * knew was users being redirected to port 8080 of their OWN machine at login.
 */
describe('a build with no realm refuses to guess one', () => {
  it('returns null when the authority is unset', () => {
    expect(readOidcConfig({}, ORIGIN)).toBeNull()
  })

  it('returns null when the authority is blank or whitespace', () => {
    expect(readOidcConfig({ VITE_OIDC_AUTHORITY: '' }, ORIGIN)).toBeNull()
    expect(readOidcConfig({ VITE_OIDC_AUTHORITY: '   ' }, ORIGIN)).toBeNull()
  })

  it('returns null for something that is not an http(s) URL', () => {
    expect(readOidcConfig({ VITE_OIDC_AUTHORITY: 'keycloak.example.internal' }, ORIGIN)).toBeNull()
    expect(readOidcConfig({ VITE_OIDC_AUTHORITY: 'javascript:alert(1)' }, ORIGIN)).toBeNull()
  })

  it('never falls back to localhost', () => {
    for (const env of [{}, { VITE_OIDC_AUTHORITY: '' }, { VITE_OIDC_AUTHORITY: 'nonsense' }]) {
      const config = readOidcConfig(env, ORIGIN) as { authority?: string } | null
      expect(config?.authority ?? '').not.toContain('localhost')
    }
  })

  it('uses the configured authority verbatim when there is one', () => {
    expect(settingsFor().authority).toBe('https://keycloak.example.internal/realms/rocketwiki')
  })
})

/**
 * The other three keep defaults on purpose: neither can be silently wrong. The
 * redirect URIs are derived from the origin the app is actually running on, and
 * a wrong client id is refused by Keycloak at the first sign-in.
 */
describe('the values that can safely default, do', () => {
  it('derives both redirect URIs from the running origin', () => {
    const settings = settingsFor()
    expect(settings.redirect_uri).toBe(`${ORIGIN}/auth/callback`)
    expect(settings.post_logout_redirect_uri).toBe(ORIGIN)
  })

  it('honours explicit redirect URIs when given', () => {
    const settings = settingsFor({ ...ENV, VITE_OIDC_REDIRECT_URI: 'https://elsewhere.test/cb' })
    expect(settings.redirect_uri).toBe('https://elsewhere.test/cb')
  })

  it('defaults the client id to the realm’s web client', () => {
    expect(settingsFor().client_id).toBe('rocketwiki-web')
  })
})
