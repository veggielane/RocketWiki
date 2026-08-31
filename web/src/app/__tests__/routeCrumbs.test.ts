import { describe, expect, it } from 'vitest'
import { router } from '../router'
import {
  ADMIN_PAGES,
  PAGE_SUBPAGES,
  SPACE_SYSTEM_PAGES,
  TOP_LEVEL_PAGES,
  crumbsFor,
  routeTitleFor,
} from '../routeCrumbs'

/**
 * The label maps drifted from the router once already, and the failure mode is
 * silent: a missing entry does not throw, it falls back to the raw URL segment.
 * That is how `/spaces/{key}/-/browse` came to read "Spaces / PROP / browse" in
 * lowercase beside properly written siblings, and how the two most-visited page
 * sub-screens (details, history) ended up with no crumb identity at all.
 *
 * So this walks the ROUTER'S OWN route table rather than a hand-written list of
 * paths — a route added without a label fails here instead of shipping.
 */

/** Every path pattern the router knows, flattened, as `/`-joined segments. */
function routePaths(): string[] {
  const paths: string[] = []
  function walk(routes: readonly { path?: string; children?: readonly unknown[] }[], prefix: string) {
    for (const route of routes) {
      const here = route.path === undefined ? prefix : `${prefix}/${route.path}`.replace(/\/+/g, '/')
      if (route.path !== undefined) paths.push(here)
      if (route.children) walk(route.children as typeof routes, here)
    }
  }
  walk(router.routes as never, '')
  return paths
}

describe('route label maps stay in step with the router', () => {
  it('names every space system page', () => {
    const segments = routePaths()
      .map((p) => /^\/spaces\/:spaceKey\/-\/([^/]+)$/.exec(p)?.[1])
      .filter((s): s is string => s !== undefined)

    expect(segments.length).toBeGreaterThan(0)
    for (const segment of segments) {
      expect(SPACE_SYSTEM_PAGES, `no crumb label for /spaces/:spaceKey/-/${segment}`).toHaveProperty(segment)
    }
  })

  it('names every admin section', () => {
    const segments = routePaths()
      .map((p) => /^\/admin\/([^/]+)$/.exec(p)?.[1])
      .filter((s): s is string => s !== undefined)

    expect(segments.length).toBeGreaterThan(0)
    for (const segment of segments) {
      expect(ADMIN_PAGES, `no crumb label for /admin/${segment}`).toHaveProperty(segment)
    }
  })

  it('names every page sub-screen except the redirect', () => {
    const segments = routePaths()
      .map((p) => /^\/pages\/:pageId\/([^/]+)$/.exec(p)?.[1])
      .filter((s): s is string => s !== undefined)

    for (const segment of segments) {
      // `properties` is a redirect to `details` — nobody lands on it, so it
      // deliberately has no label of its own.
      if (segment === 'properties') continue
      expect(PAGE_SUBPAGES, `no crumb label for /pages/:pageId/${segment}`).toHaveProperty(segment)
    }
    expect(PAGE_SUBPAGES).not.toHaveProperty('properties')
  })
})

/** A resolved space, as the shell hands one to `crumbsFor`. */
const PROP = { key: 'PROP', name: 'Propulsion' }

describe('crumbsFor', () => {
  it('writes out a space system page rather than echoing the URL segment', () => {
    expect(crumbsFor('/spaces/PROP/-/browse').map((c) => c.label)).toEqual(['Spaces', 'PROP', 'Browse pages'])
    expect(crumbsFor('/spaces/PROP/-/analytics').map((c) => c.label)).toEqual(['Spaces', 'PROP', 'Analytics'])
  })

  it('gives the details and history screens a crumb of their own', () => {
    // Both fell through to a bare space crumb, so the two most-visited page
    // sub-screens said nothing about being sub-screens.
    expect(crumbsFor('/pages/p1/details', PROP).map((c) => c.label)).toEqual(['Spaces', 'Propulsion', 'Details'])
    expect(crumbsFor('/pages/p1/history', PROP).map((c) => c.label)).toEqual(['Spaces', 'Propulsion', 'History'])
  })

  it('keeps the space crumb a LINK on a page route', () => {
    // The space's `to` used to be dropped, and the breadcrumb rendered the last
    // crumb as plain text — so every page view was a dead end whose only
    // working link was "Spaces", all the way to the root.
    const crumbs = crumbsFor('/spaces/PROP/stage-two-ignition-anomaly', PROP)
    expect(crumbs.at(-1)?.to).toBe('/spaces/PROP')

    const byId = crumbsFor('/pages/p1', PROP)
    expect(byId.at(-1)?.to).toBe('/spaces/PROP')
  })

  it('calls a space-level role assignment "Grants", as design.md §6.5 does', () => {
    // The link on the space settings screen and the crumb for the same
    // destination used to disagree — "Grants" versus "Permissions", which is
    // also the name of the page-level screen.
    expect(SPACE_SYSTEM_PAGES.grants).toBe('Grants')
    expect(PAGE_SUBPAGES.permissions).toBe('Permissions')
  })

  it("labels the space with its NAME, not its key or the URL's casing", () => {
    // `PageHeader` states "always the NAME, never the key", and the crumb
    // directly above it was rendering the key — so the trash screen read
    // "Spaces / PROP / Trash" over a header saying "Trash / Propulsion", the
    // exact pair that rule exists to prevent.
    expect(crumbsFor('/spaces/prop/-/trash', PROP).map((c) => c.label)).toEqual(['Spaces', 'Propulsion', 'Trash'])
    // The link still points at the URL's own key: it resolves either way, and
    // rewriting it here would be a redirect disguised as a label.
    expect(crumbsFor('/spaces/prop/-/trash', PROP)[1]?.to).toBe('/spaces/prop')
  })

  it('falls back to the URL key when no space has resolved yet', () => {
    expect(crumbsFor('/spaces/eng/-/trash').map((c) => c.label)).toEqual(['Spaces', 'eng', 'Trash'])
  })

  it('stops at the space for a page route rather than guessing a title from a slug', () => {
    expect(crumbsFor('/spaces/PROP/stage-two-ignition-anomaly').map((c) => c.label)).toEqual(['Spaces', 'PROP'])
  })

  it('names the top-level screens', () => {
    for (const [segment, label] of Object.entries(TOP_LEVEL_PAGES)) {
      expect(crumbsFor(`/${segment}`).map((c) => c.label)).toEqual([label])
    }
    expect(crumbsFor('/-/docs').map((c) => c.label)).toEqual(['Help'])
  })
})

describe('routeTitleFor', () => {
  it('is the last crumb — the thing you are looking at', () => {
    expect(routeTitleFor('/admin/audit')).toBe('Audit log')
    expect(routeTitleFor('/spaces/PROP/-/trash')).toBe('Trash')
    // The home route is the feed homepage now, not the space list.
    expect(routeTitleFor('/')).toBe('Home')
  })
})
