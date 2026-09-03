/**
 * Where a person's profile lives (app/router.tsx `people/:userId`). Takes the
 * local user id every `UserRef` carries and `me.localUserId` reports — never
 * the token subject, which is a different identifier for the same person.
 *
 * **`/people`, not `/users`, and the choice is load-bearing.** The API owns
 * `GET /users/{id}/avatar`, and both the Vite dev proxy (web/vite.config.ts
 * `proxyPaths`) and the nginx image forward the whole `/users` prefix to it.
 * A route under `/users` works when reached by client-side navigation and
 * 404s from the API on a direct load or a refresh — the first version of this
 * file did exactly that, and no jsdom test could see it. `__tests__/
 * profilePath.test.ts` reads the proxy table off disk so the next collision
 * fails here instead of in a browser.
 *
 * Its own module rather than a second export of UserLink.tsx so that file
 * stays component-only (fast refresh) and the rail can build the link to the
 * viewer's own profile without rendering a UserLink.
 */
export function profilePath(userId: string): string {
  return `/people/${encodeURIComponent(userId)}`
}
