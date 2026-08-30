import type { AuthProviderProps } from 'react-oidc-context'
import { InMemoryWebStorage, WebStorageStateStore } from 'oidc-client-ts'

/**
 * Authorization Code + PKCE against Keycloak (design.md §11). Values come
 * entirely from env vars — see `.env.example`. Exercised end to end against a
 * live realm on 2026-08-28; the store split below is what that run fixed.
 */

/** Environment shape this module reads. `import.meta.env` satisfies it. */
export type OidcEnv = Record<string, unknown>

function optional(env: OidcEnv, name: string, fallback: string): string {
  const value = env[name]
  return typeof value === 'string' && value.length > 0 ? value : fallback
}

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

/**
 * The realm this build talks to, or `null` when nobody said.
 *
 * **`VITE_OIDC_AUTHORITY` fails closed**, like `VITE_DRAWIO_URL` and the OTLP
 * endpoint. It used to fall back to `http://localhost:8080/realms/rocketwiki`,
 * which is the one kind of default that cannot be right anywhere but one
 * developer's machine: an image built without the build-arg looked completely
 * healthy, and the first anyone knew of it was users being redirected to
 * `localhost` at login — sending an authorization request, and whatever the
 * browser would do with the response, to whatever happens to be listening on
 * port 8080 of the *user's own* machine. A build that does not know its realm
 * must say so, not guess.
 *
 * The other three keep defaults deliberately, because none of them can be
 * silently wrong: the redirect URIs are derived from the origin the app is
 * actually running on, and a wrong `client_id` is refused by Keycloak with
 * `invalid_client` at the first sign-in rather than quietly working.
 */
export function readOidcConfig(env: OidcEnv, origin: string): AuthProviderProps | null {
  const raw = env['VITE_OIDC_AUTHORITY']
  if (typeof raw !== 'string') return null
  const authority = raw.trim()
  if (authority.length === 0) return null
  try {
    const parsed = new URL(authority)
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null
  } catch {
    // A typo'd authority disables sign-in rather than handing an unparseable
    // string to the OIDC client — same posture as the draw.io URL.
    return null
  }

  return {
    authority,
    client_id: optional(env, 'VITE_OIDC_CLIENT_ID', 'rocketwiki-web'),
    redirect_uri: optional(env, 'VITE_OIDC_REDIRECT_URI', `${origin}/auth/callback`),
    post_logout_redirect_uri: optional(env, 'VITE_OIDC_POST_LOGOUT_REDIRECT_URI', origin),
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
}

/** `null` when this build has no realm configured — App renders the explanation instead. */
export const oidcConfig = readOidcConfig(import.meta.env as OidcEnv, window.location.origin)
