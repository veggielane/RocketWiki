/**
 * Where a person's profile lives (app/router.tsx `users/:userId`). Takes the
 * local user id every `UserRef` carries and `me.localUserId` reports — never
 * the token subject, which is a different identifier for the same person.
 *
 * Its own module rather than a second export of UserLink.tsx so that file
 * stays component-only (fast refresh) and the rail can build the link to the
 * viewer's own profile without rendering a UserLink.
 */
export function profilePath(userId: string): string {
  return `/users/${encodeURIComponent(userId)}`
}
