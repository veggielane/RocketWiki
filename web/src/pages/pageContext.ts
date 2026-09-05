import { createContext, useContext } from 'react'

/**
 * The page a rendered fence is sitting on.
 *
 * A fence is rendered by TipTap deep inside the page view, so it cannot reach the id
 * through props. `useParams` is not an option either: a page has two addresses, and on
 * `/spaces/{key}/{slug}` the route param is the slug — the id has already been resolved
 * by then and is only held by the view.
 *
 * Null outside a page (the editor's own preview, a test harness), which consumers must
 * tolerate rather than assume: a widget with no page to belong to degrades instead of
 * querying with an empty id.
 *
 * No provider component here on purpose — React 19 renders a context directly, and a
 * one-line wrapper would make this a module exporting both a component and a hook, which
 * is the shape that breaks fast refresh.
 */
export const PageIdContext = createContext<string | null>(null)

export function useCurrentPageId(): string | null {
  return useContext(PageIdContext)
}
