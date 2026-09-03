import { MarkViewContent } from '@tiptap/react'
import type { MarkViewProps } from '@tiptap/core'
import { Link as RouterLink, useInRouterContext } from 'react-router-dom'
import { MISSING_LINK_TITLE, protectedLinkTitle } from '../../access/denial/protectedCopy'
import { usePageLinkTarget } from './pageLinkTargets'

/**
 * Rendering for the pageLink mark (design.md §4's `[title](page://{id})`).
 *
 * In page view (the read-only editor — the one-renderer rule) each link is
 * resolved through the page's own `linkTargets`, which rode on the page read
 * (§6.7 / §21.8): a readable target becomes a router link to its readable
 * address; a withheld one stays the author's text — replacing it with the
 * placeholder would erase content the author wrote — followed by the
 * server's placeholder marker, with the marking label (or the space
 * sentence) on hover and for a screen reader, never inline; a target that no
 * longer exists gets a marker saying so. The label reaches a reader only on
 * request, because a page of prose is not the place to print every linked
 * page's classification.
 *
 * In EDIT mode the mark is a plain styled span and fetches nothing: link
 * state has no business re-rendering under the caret, and the text stays
 * ordinarily editable. Outside a page view (a comment, a help topic) nothing
 * resolved anything, so the link goes to the page's id address and the route
 * there answers honestly.
 *
 * `useInRouterContext` guards the router link: this view is rendered wherever
 * the read-only editor is, and a bare `<a>` is the right fallback for a
 * surface mounted without a router rather than a thrown hook.
 */
export function PageLinkView({ mark, editor }: MarkViewProps) {
  const pageId = String(mark.attrs.pageId ?? '')
  const editable = editor.isEditable
  const target = usePageLinkTarget(pageId)
  const inRouter = useInRouterContext()

  if (editable) {
    return (
      <span className="rw-page-link">
        <MarkViewContent as="span" />
      </span>
    )
  }

  if (target?.kind === 'protected') {
    const title = protectedLinkTitle(target.markingLabel)
    return (
      <span className="rw-page-link rw-page-link-protected" title={title}>
        <MarkViewContent as="span" />
        <span className="rw-page-link-marker" aria-hidden="true">
          {target.placeholderTitle}
        </span>
        <span className="rw-visually-hidden">{title}</span>
      </span>
    )
  }

  if (target?.kind === 'missing') {
    return (
      <span className="rw-page-link rw-page-link-missing" title={MISSING_LINK_TITLE}>
        <MarkViewContent as="span" />
        <span className="rw-page-link-marker" aria-hidden="true">
          !
        </span>
        <span className="rw-visually-hidden">{MISSING_LINK_TITLE}</span>
      </span>
    )
  }

  // Readable, or unresolved (no page view around this render, or an id the
  // page did not list): a real address either way. The readable one is the
  // slug route; the fallback is the id route, which resolves — and discloses
  // or refuses — on arrival.
  const href =
    target?.kind === 'readable' ? `/spaces/${target.page.spaceKey}/${target.page.slug}` : `/pages/${pageId}`

  if (inRouter) {
    return (
      <RouterLink className="rw-page-link" to={href}>
        <MarkViewContent as="span" />
      </RouterLink>
    )
  }
  return (
    <a className="rw-page-link" href={href}>
      <MarkViewContent as="span" />
    </a>
  )
}
