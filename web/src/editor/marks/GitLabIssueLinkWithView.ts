import { ReactMarkViewRenderer } from '@tiptap/react'
import { GitLabIssueLink } from './GitLabIssueLink'
import { GitLabIssueLinkView } from './GitLabIssueLinkView'

/**
 * The gitlabIssueLink mark the real editor uses: identical
 * name/attrs/schema to the plain `GitLabIssueLink` (same swap pattern as
 * DrawioDiagram → DrawioDiagramWithView — rendering only, never
 * serialization), plus a React mark view that shows the live chip in page
 * view (see GitLabIssueLinkView.tsx). Kept out of `editorExtensions` so the
 * headless round-trip suite never touches React or the API.
 */
export const GitLabIssueLinkWithView = GitLabIssueLink.extend({
  addMarkView() {
    return ReactMarkViewRenderer(GitLabIssueLinkView)
  },
})
