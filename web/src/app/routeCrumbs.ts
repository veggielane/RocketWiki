import { SYSTEM_SEGMENT } from '../pages/pageSlug'

export interface Crumb {
  label: string
  /** Absent on the last crumb, which is where you already are. */
  to?: string
}

const SPACES: Crumb = { label: 'Spaces', to: '/' }
const ADMIN: Crumb = { label: 'Admin', to: '/admin' }

/**
 * The system pages a space owns, keyed by the segment after `/-/`.
 *
 * Every key here must exist in app/router.tsx and vice versa — a missing entry
 * does not fail, it silently renders the raw URL segment ("browse", "analytics")
 * next to properly written siblings, which is exactly how this map drifted out of
 * step with the router once already. `routeCrumbs.test.ts` walks the router's own
 * route table against these maps so the next omission is a failing test rather
 * than a lowercase word in the chrome.
 */
export const SPACE_SYSTEM_PAGES: Record<string, string> = {
  admin: 'Space settings',
  analytics: 'Analytics',
  browse: 'Browse pages',
  // design.md §6.5's own word for a space-level role assignment. The page-level
  // screen is "Permissions"; these are different things and the chrome must not
  // call them the same one.
  grants: 'Grants',
  trash: 'Trash',
  'import-report': 'Import report',
}

/**
 * A page's own sub-screens. `properties` is deliberately absent: that address is
 * now only a redirect to `details` (router.tsx), and naming it here would put a
 * crumb on a URL nobody lands on.
 */
export const PAGE_SUBPAGES: Record<string, string> = {
  edit: 'Editing',
  details: 'Details',
  history: 'History',
  permissions: 'Permissions',
}

export const ADMIN_PAGES: Record<string, string> = {
  analytics: 'Analytics',
  audit: 'Audit log',
  sync: 'Sync status',
  groups: 'Groups',
  users: 'Users',
  emojis: 'Emoji',
  'property-keys': 'Property keys',
}

/** Top-level routes that are their own whole screen. */
export const TOP_LEVEL_PAGES: Record<string, string> = {
  search: 'Search',
  ask: 'Ask the wiki',
  settings: 'Settings',
}

/**
 * Crumbs for everything addressed by its own path.
 *
 * A page route stops at its space rather than naming the page. The shell knows
 * a page's slug, not its title, and de-slugifying one would put a *guess* at a
 * page's name in the chrome — `stage-two-ignition-anomaly` is not
 * "Stage two ignition anomaly review". The page's own heading says the title
 * a line below, so the crumb spends itself on the thing the heading does not
 * repeat: which space you are in.
 */
/** The space a route is in, resolved from the server rather than from the URL. */
export interface CanonicalSpace {
  key: string
  name: string
}

export function crumbsFor(pathname: string, canonicalSpace?: CanonicalSpace): Crumb[] {
  const [first, ...rest] = pathname.split('/').filter(Boolean)

  if (first === undefined) return [{ label: 'Spaces' }]

  if (first === 'spaces') {
    const [key, second, third] = rest
    if (key === undefined) return [{ label: 'Spaces' }]
    if (key === 'new') return [SPACES, { label: 'New space' }]
    if (key === 'archived') return [SPACES, { label: 'Archived spaces' }]
    // The space's NAME where the caller could resolve one, the URL's key only
    // as a fallback. Two reasons: `PageHeader` states "always the NAME, never
    // the key" and the crumb directly above it was contradicting that; and URLs
    // are case-insensitive, so a crumb echoing whichever casing was typed
    // labelled the same place two ways. The `to` keeps the URL's own key, which
    // resolves either way — rewriting it here would be a redirect disguised as
    // a label.
    const spaceLabel = canonicalSpace?.name ?? decodeURIComponent(key)
    const space: Crumb = { label: spaceLabel, to: `/spaces/${key}` }
    if (second === SYSTEM_SEGMENT && third !== undefined) {
      return [SPACES, space, { label: SPACE_SYSTEM_PAGES[third] ?? third }]
    }
    // `/spaces/{key}/{slug}` — a page. The space crumb keeps its `to`: it used
    // to be dropped, and since the breadcrumb rendered the last crumb as plain
    // text, EVERY page view was a dead end whose only working link was "Spaces"
    // — all the way to the root. The rail's caption-sized "Browse all pages"
    // was the sole path back.
    return [SPACES, space]
  }

  if (first === 'pages') {
    // Name for the label, key for the address — a `/pages/{id}` URL carries
    // neither, so both come from the resolved space.
    const space: Crumb | undefined =
      canonicalSpace === undefined
        ? undefined
        : { label: canonicalSpace.name, to: `/spaces/${encodeURIComponent(canonicalSpace.key)}` }
    const sub = rest[1] === undefined ? undefined : PAGE_SUBPAGES[rest[1]]
    if (sub === undefined) {
      // Same as the slug route: the space stays a link, not a dead final crumb.
      return space === undefined ? [{ label: 'Spaces' }] : [SPACES, space]
    }
    return space === undefined ? [SPACES, { label: sub }] : [SPACES, space, { label: sub }]
  }

  if (first === 'admin') {
    const section = rest[0]
    if (section === undefined) return [{ label: 'Admin' }]
    return [ADMIN, { label: ADMIN_PAGES[section] ?? section }]
  }

  if (first === SYSTEM_SEGMENT && rest[0] === 'docs') {
    return [{ label: 'Help' }]
  }

  return [{ label: TOP_LEVEL_PAGES[first] ?? first }]
}

/**
 * The route's own name for itself, for the browser tab when the screen has not
 * supplied a better one (app/documentTitle.ts). The last crumb, because that is
 * the thing you are looking at.
 */
export function routeTitleFor(pathname: string, canonicalSpace?: CanonicalSpace): string {
  const crumbs = crumbsFor(pathname, canonicalSpace)
  return crumbs[crumbs.length - 1]?.label ?? 'RocketWiki'
}
