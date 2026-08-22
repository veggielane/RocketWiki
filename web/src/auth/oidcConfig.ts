import type { AuthProviderProps } from 'react-oidc-context'
import { InMemoryWebStorage, WebStorageStateStore } from 'oidc-client-ts'

/**
 * Keycloak isn't running yet (backend + infra are being scaffolded
 * concurrently). This wiring is correct for Authorization Code + PKCE
 * against a real Keycloak realm, but has never been exercised against a
 * live server. Values come entirely from env vars — see `.env.example`.
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
 * Tokens must stay in memory only (design.md §11 / senior-frontend-engineer
 * brief) — never sessionStorage or localStorage, where they'd survive an
 * XSS payload or be readable by any script on the page. `InMemoryWebStorage`
 * backs both the OIDC user store (id/access/refresh token) and, separately,
 * silent-renew state. A page refresh means a fresh silent sign-in via the
 * `silent_redirect_uri` iframe flow, which is the accepted tradeoff.
 */
const inMemoryStore = new WebStorageStateStore({ store: new InMemoryWebStorage() })

export const oidcConfig: AuthProviderProps = {
  authority,
  client_id: clientId,
  redirect_uri: redirectUri,
  post_logout_redirect_uri: postLogoutRedirectUri,
  response_type: 'code',
  scope: 'openid profile email',
  userStore: inMemoryStore,
  stateStore: inMemoryStore,
  automaticSilentRenew: true,
  onSigninCallback: () => {
    // Strip the `code`/`state` query params Keycloak appended after the
    // redirect so a refresh doesn't replay the auth code.
    window.history.replaceState({}, document.title, window.location.pathname)
  },
}
