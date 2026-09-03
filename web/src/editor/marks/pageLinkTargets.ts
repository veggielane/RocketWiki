import { createContext, useContext } from 'react'

/**
 * What one `page://{id}` link in a page resolved to, from the page's own
 * `linkTargets` (design.md §6.7 / §21.8). The ids are extracted server-side
 * from content the caller was already allowed to read, and each resolves to
 * exactly one of these.
 */
export type PageLinkTargetState =
  | { kind: 'readable'; page: { id: string; title: string; slug: string; spaceKey: string } }
  /** The target exists and is withheld: the server's placeholder title, and the marking label when the caller holds access to the space. */
  | { kind: 'protected'; placeholderTitle: string; markingLabel: string | null }
  /** Neither a page nor a denial came back — the target no longer exists. */
  | { kind: 'missing' }

/** The shape of one `Page.linkTargets` entry, as the page view's query selects it. Structural, so the generated type satisfies it. */
export interface PageLinkTargetLike {
  id: string
  page: { id: string; title: string; slug: string; spaceKey: string } | null
  denial: { placeholderTitle: string; marking: { label: string } | null } | null
}

export function buildPageLinkTargetMap(targets: readonly PageLinkTargetLike[]): ReadonlyMap<string, PageLinkTargetState> {
  const map = new Map<string, PageLinkTargetState>()
  for (const target of targets) {
    if (target.page) {
      map.set(target.id, { kind: 'readable', page: target.page })
    } else if (target.denial) {
      map.set(target.id, {
        kind: 'protected',
        placeholderTitle: target.denial.placeholderTitle,
        markingLabel: target.denial.marking?.label ?? null,
      })
    } else {
      map.set(target.id, { kind: 'missing' })
    }
  }
  return map
}

/**
 * The resolved targets of the page being rendered, for the link mark view.
 * Null outside a page view — a rendered comment, a help topic, the editor's
 * own preview — where nothing resolved anything, and a link falls back to the
 * page's id address rather than pretending to know its state.
 *
 * No provider component here on purpose — React 19 renders a context
 * directly, and a wrapper would make this a module exporting both a
 * component and a hook, which is the shape that breaks fast refresh (same
 * reason as pages/pageContext.ts).
 */
export const PageLinkTargetsContext = createContext<ReadonlyMap<string, PageLinkTargetState> | null>(null)

/**
 * `undefined` when nothing resolved links here (no context); `null` when the
 * page resolved its links and this id was not among them (content changed
 * under the view, or an id the scanner could not parse) — both fall back to
 * the id address.
 */
export function usePageLinkTarget(pageId: string): PageLinkTargetState | null | undefined {
  const targets = useContext(PageLinkTargetsContext)
  if (targets === null) return undefined
  return targets.get(pageId) ?? null
}
