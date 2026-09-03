/**
 * The words the SPA adds around a withheld page (design.md §6.7 / §21.8), in
 * one place so the page screen, a tree leaf and a link marker say the same
 * thing about the same state.
 *
 * What is deliberately NOT here: the placeholder title itself. `(protected)`
 * is a server constant (`AccessDenial.placeholderTitle`, `ProtectedTreeNode.title`)
 * and every surface renders the value it was sent, verbatim — one answer to
 * "what does a withheld page read as", owned by the side that also decides
 * what is withheld. Likewise the marking label: the server's one formatter
 * builds it, and these strings only ever sit beside it.
 */

/** The heading of the page-sized placeholder. Never the page's own title, which the caller may not know. */
export const PROTECTED_PAGE_TITLE = 'Protected page'

/** The single sentence a caller with no access grant in the space is told — and all they are told. */
export const NO_SPACE_ACCESS = 'You have no access to this space.'

/** Above a tree whose every page reads as protected because the caller holds no access grant there. */
export const PROTECTED_TREE_NOTE = 'You have no access to this space, so its pages are shown as protected.'

/** Heads the list of failing gates on the page-sized placeholder. */
export const WHY_PROTECTED_HEADING = 'Why you cannot read this page'

/** The one focusable control on a protected tree leaf: the disclosure that lists its reasons. */
export const WHY_PROTECTED_BUTTON = 'Why is this page protected?'

/**
 * What a protected in-page link says on hover and to a screen reader. The
 * label when the marking is disclosed (the caller holds access to the space,
 * so they may know how the page is marked), the space sentence when it is
 * withheld.
 */
export function protectedLinkTitle(markingLabel: string | null | undefined): string {
  return markingLabel ? `Protected page, marked ${markingLabel}.` : NO_SPACE_ACCESS
}

/** An in-page link whose target no longer exists — neither a page nor a denial came back for it. */
export const MISSING_LINK_TITLE = 'This page no longer exists.'

/** On space settings, for a manager whose own access grant does not exist (§6.4 — managing without reading is a normal state). */
export const MANAGE_WITHOUT_ACCESS_NOTE =
  'You can manage this space but hold no access grant in it, so its pages show as protected to you.'

/** Above the two grant sections, saying which kind answers which question (§6.4). */
export const GRANT_KINDS_EXPLANATION =
  "Access grants decide who may see this space's pages. Role grants decide who may edit or administer it, and confer no visibility on their own."
