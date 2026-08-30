import { useSpaceListQuery, usePageSpaceRefQuery } from '../graphql/generated/graphql'
import { sameSpaceKey } from '../pages/pageSlug'

/**
 * The server's spelling of the space key for whatever route this is, or
 * `undefined` while it is unknown.
 *
 * URLs are case-insensitive: `/spaces/eng/...` resolves to the same space as
 * `/spaces/ENG/...`. Anything DISPLAYING the key therefore cannot read it out of
 * the URL, or the same space wears whichever casing the visitor happened to
 * type — beside a rail picker showing the canonical name, which is how a
 * correct URL comes to look like a bug.
 *
 * Two routes, two sources, both already cached:
 *  - `/pages/{id}` carries no key at all, so it comes from the page (the shell
 *    and the breadcrumb already run this query, and urql serves the second
 *    caller from cache).
 *  - `/spaces/{key}/…` has one, but in the visitor's casing — matched
 *    case-insensitively against the space list the rail loads on every route.
 *
 * Returns `undefined` rather than the URL's version when nothing resolves, so
 * the caller decides what to fall back to. A key that matches no visible space
 * is not a failure worth reporting: §6.7 makes a space you cannot see
 * indistinguishable from one that does not exist, and this is a label.
 */
export function useCanonicalSpaceKey(pathname: string): string | undefined {
  return useCanonicalSpace(pathname)?.key
}

/**
 * The space this route is in, as the SERVER spells it — key and name.
 *
 * The name matters because `PageHeader` states the rule explicitly ("always the
 * space/page NAME, never its key") while the breadcrumb directly above it was
 * still rendering the key: the trash screen read `Spaces / PROP / Trash` in the
 * crumb and "Trash / Propulsion" in the header, which is the exact pair that
 * rule was written to stop, stacked vertically instead of side by side.
 */
export function useCanonicalSpace(pathname: string): { key: string; name: string } | undefined {
  const pageId = /^\/pages\/([^/]+)/.exec(pathname)?.[1]
  const urlSpaceKey = /^\/spaces\/([^/]+)/.exec(pathname)?.[1]
  const decodedUrlKey = urlSpaceKey ? decodeURIComponent(urlSpaceKey) : undefined

  const [{ data: pageRef }] = usePageSpaceRefQuery({ variables: { id: pageId ?? '' }, pause: !pageId })
  // The list is what carries names, and the rail loads it on every route, so
  // both arms below are served from cache.
  const [{ data: spaceList }] = useSpaceListQuery({ pause: !decodedUrlKey && !pageId })

  // A page route names no space in its URL; the page read supplies the key,
  // and the list turns it into a name.
  const key = pageId ? (pageRef?.page?.spaceKey ?? undefined) : decodedUrlKey
  if (!key) return undefined
  // `new` and `archived` are sibling routes, not space keys — they match no
  // space and correctly fall through to undefined.
  return spaceList?.spaces.find((space) => sameSpaceKey(space.key, key))
}
