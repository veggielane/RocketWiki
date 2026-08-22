/**
 * The urql `Client` is created once, outside the React tree, but the bearer
 * token lives in `react-oidc-context`'s React state (in memory only — see
 * auth/oidcConfig.ts). This tiny module bridges the two: `AuthTokenSync`
 * (mounted once near the app root) pushes the current token here on every
 * change, and urql's `fetchOptions` reads it per-request. Nothing here
 * persists the token beyond this in-memory variable.
 */
let currentAccessToken: string | undefined

export function setAccessToken(token: string | undefined): void {
  currentAccessToken = token
}

export function getAccessToken(): string | undefined {
  return currentAccessToken
}
