/**
 * Which presence room a route belongs to.
 *
 * You see the people looking at the same screen as you, so the room is the
 * screen — and "same screen" has to mean "same layout", or "who else is here"
 * would list people looking at something else.
 *
 * Three kinds of answer:
 *
 *   `null`   — a PAGE screen, whose room this function cannot know. The room is
 *              `page:{id}`, and the id is what the route may not carry: the
 *              readable address `/spaces/ENG/runbook` names a slug, and only the
 *              screen that resolved it knows the id. Returning null rather than
 *              guessing a path-shaped room is what lets a reader at
 *              `/spaces/ENG/runbook` and an editor at `/pages/{id}/edit` share
 *              one room, and it avoids a join to a throwaway room on the render
 *              before the screen supplies the real one.
 *   `space:…` — the screens that are ABOUT a space rather than about a page.
 *   `site:…`  — everything else, one room per screen.
 */

/** The `-/` screens under a space: about the space, shared by everyone looking at it. */
const SPACE_SCOPED = new Set(['browse', 'admin', 'analytics', 'grants', 'trash', 'import-report'])

/**
 * `/spaces/…` paths that are NOT a space key, and must not be read as one.
 * `/spaces/new` would otherwise become the room `space:NEW`.
 */
const SPACES_LITERALS = new Set(['new', 'archived'])

/**
 * Page screens, by their first path segment. Each resolves a page id and
 * overrides the room itself (`useSetPresenceRoom`).
 */
const PAGE_ROUTES = new Set(['pages'])

export function presenceRoomFor(pathname: string): string | null {
  const segments = pathname.split('/').filter((segment) => segment.length > 0)

  // `/pages/{id}` and everything under it — view, edit, history, details,
  // properties, permissions. All page screens; all override.
  if (segments.length > 0 && PAGE_ROUTES.has(segments[0])) return null

  if (segments[0] === 'spaces') {
    const second = segments[1]
    // `/spaces` itself, and the two literal screens, are ordinary site screens.
    if (second === undefined || SPACES_LITERALS.has(second.toLowerCase())) return sitePathRoom(pathname)

    // Space keys are case-insensitive everywhere else in the app (the server
    // canonicalises them, and `sameSpaceKey` is how the SPA compares them), so
    // two people at /spaces/eng and /spaces/ENG are on the same screen and must
    // be in the same room. Upper-cased rather than lower, to match how a space
    // key is written.
    const spaceKey = second.toUpperCase()

    // `/spaces/{key}/-/{screen}` — about the space.
    if (segments[2] === '-' && segments[3] !== undefined && SPACE_SCOPED.has(segments[3].toLowerCase())) {
      return `space:${spaceKey}:${segments[3].toLowerCase()}`
    }

    // `/spaces/{key}` (the space's home page) and `/spaces/{key}/{slug}` are
    // page screens: both render a page, and only they can resolve which.
    return null
  }

  return sitePathRoom(pathname)
}

/**
 * One room per screen, from the path itself.
 *
 * Lower-cased and trailing-slash-stripped so `/Search` and `/search/` do not
 * split a room. The whole path is kept rather than a route pattern, because the
 * only parameterised screen that reaches here is a help topic — and two people
 * reading DIFFERENT topics are not looking at the same screen, whatever the
 * layout has in common.
 */
function sitePathRoom(pathname: string): string {
  const normalized = pathname.toLowerCase().replace(/\/+$/, '')
  return `site:${normalized === '' ? '/' : normalized}`
}

/** The room a page screen sets for itself, wherever it was reached from. */
export function pageRoom(pageId: string): string {
  return `page:${pageId}`
}
