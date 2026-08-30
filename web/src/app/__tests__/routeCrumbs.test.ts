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

describe('crumbsFor', () => {
  it('writes out a space system page rather than echoing the URL segment', () => {
    expect(crumbsFor('/spaces/PROP/-/browse').map((c) => c.label)).toEqual(['Spaces', 'PROP', 'Browse pages'])
    expect(crumbsFor('/spaces/PROP/-/analytics').map((c) => c.label)).toEqual(['Spaces', 'PROP', 'Analytics'])
  })

  it('gives the details and history screens a crumb of their own', () => {
    // Both fell through to a bare space crumb, so the two most-visited page
    // sub-screens said nothing about being sub-screens.
    expect(crumbsFor('/pages/p1/details', 'PROP').map((c) => c.label)).toEqual(['Spaces', 'PROP', 'Details'])
    expect(crumbsFor('/pages/p1/history', 'PROP').map((c) => c.label)).toEqual(['Spaces', 'PROP', 'History'])
  })

  it('calls a space-level role assignment "Grants", as design.md §6.5 does', () => {
    // The link on the space settings screen and the crumb for the same
    // destination used to disagree — "Grants" versus "Permissions", which is
    // also the name of the page-level screen.
    expect(SPACE_SYSTEM_PAGES.grants).toBe('Grants')
    expect(PAGE_SUBPAGES.permissions).toBe('Permissions')
  })

  it('prefers the server-canonical space key over the casing in the URL', () => {
    // URLs are case-insensitive now, so /spaces/eng and /spaces/ENG are one
    // space. A crumb echoing whichever casing was typed would label the same
    // place two ways, beside a rail picker showing the canonical name.
    expect(crumbsFor('/spaces/eng/-/trash', 'ENG').map((c) => c.label)).toEqual(['Spaces', 'ENG', 'Trash'])
    // The link still points at the URL's own key: it resolves either way, and
    // rewriting it here would be a redirect disguised as a label.
    expect(crumbsFor('/spaces/eng/-/trash', 'ENG')[1]?.to).toBe('/spaces/eng')
  })

  it('falls back to the URL key when no canonical one is known', () => {
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
    expect(routeTitleFor('/')).toBe('Spaces')
  })
})
