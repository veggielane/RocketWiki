import { ReactMarkViewRenderer } from '@tiptap/react'
import { PageLink } from './PageLink'
import { PageLinkView } from './PageLinkView'

/**
 * The pageLink mark the real editor uses: identical name/attrs/schema to the
 * plain `PageLink` (same swap pattern as GitLabIssueLink →
 * GitLabIssueLinkWithView — rendering only, never serialization), plus a
 * React mark view that resolves the target in page view (see
 * PageLinkView.tsx). Kept out of `editorExtensions` so the headless
 * round-trip suite never touches React or the router.
 */
export const PageLinkWithView = PageLink.extend({
  addMarkView() {
    return ReactMarkViewRenderer(PageLinkView)
  },
})
