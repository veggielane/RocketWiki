import type { AuthProviderProps } from 'react-oidc-context'
import { InMemoryWebStorage, WebStorageStateStore } from 'oidc-client-ts'

/**
 * Authorization Code + PKCE against Keycloak (design.md §11). Values come
 * entirely from env vars — see `.env.example`. Exercised end to end against a
 * live realm on 2026-08-28; the store split below is what that run fixed.
 */
function requireEnv(name: string, fallback: string): string {
  const value = import.meta.env[name] as string | undefined
  return value && value.length > 0 ? value : fallback
}

const authority = requireEnv('VITE_OIDC_AUTHORITY', 'http://localhost:8080/realms/rocketwiki')
const clientId = requireEnv('VITE_OIDC_CLIENT_ID', 'rocketwiki-web')
const redirectUri = requireEnv('VITE_OIDC_REDIRECT_URI', window.location.origin + '/auth/callback')
const postLogoutRedirectUri = requireEnv('VITE_OIDC_POST_LOGOUT_REDIRECT_URI', window.location.origin)

/**
 * Tokens must stay in memory only (design.md §11) — never sessionStorage or
 * localStorage, where they'd survive an XSS payload or be readable by any
 * script on the page. A page refresh means a fresh silent sign-in via the
 * `silent_redirect_uri` iframe flow, which is the accepted tradeoff.
 */
const userStore = new WebStorageStateStore({ store: new InMemoryWebStorage() })

/**
 * The *state* store deliberately does NOT share the user store's in-memory
 * backing, and cannot: sign-in state is written before the browser navigates
 * away to Keycloak and read back after it navigates home, and a full page
 * navigation destroys anything held in memory. Pointing both at
 * `InMemoryWebStorage` made the code exchange fail every single time —
 * Keycloak returned a perfectly good `code`, oidc-client-ts looked up the
 * matching state, found an empty store, and the SPA sat on "Completing
 * sign-in…" forever. It reproduced on the first real login this project ever
 * performed and was invisible before that, because the flow was only ever
 * exercised against mocks that never reload the page.
 *
 * This does not weaken §11. What lives here is the transient PKCE
 * `code_verifier`, `state` and `nonce` for one in-flight sign-in — not a
 * token. The verifier is useless without the matching single-use
 * authorization code, and oidc-client-ts clears the entry as soon as the
 * exchange completes. `sessionStorage` (not `localStorage`) keeps even that
 * scoped to the one tab and gone when it closes.
 */
const stateStore = new WebStorageStateStore({ store: window.sessionStorage })

export const oidcConfig: AuthProviderProps = {
  authority,
  client_id: clientId,
  redirect_uri: redirectUri,
  post_logout_redirect_uri: postLogoutRedirectUri,
  response_type: 'code',
  scope: 'openid profile email',
  userStore,
  stateStore,
  automaticSilentRenew: true,
  onSigninCallback: () => {
    // Strip the `code`/`state` query params Keycloak appended after the
    // redirect so a refresh doesn't replay the auth code.
    window.history.replaceState({}, document.title, window.location.pathname)
  },
}
