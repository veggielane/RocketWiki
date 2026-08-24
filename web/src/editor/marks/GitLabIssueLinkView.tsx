import { MarkViewContent } from '@tiptap/react'
import type { MarkViewProps } from '@tiptap/core'
import { useGitLabIssueQuery, useGitLabStatusQuery } from '../../graphql/generated/graphql'
import { describeGitLabUnavailable } from '../../feedback/unavailableCopy'

/**
 * Live rendering for the gitlabIssueLink mark (design.md §18). Page view
 * (read-only editor — the one-renderer rule) shows a chip: the author's
 * link text plus a live open/closed badge, linking out to the issue's
 * webUrl. Degradation is §18's vocabulary: any `unavailable` reason renders
 * the plain link text with a subtle marker whose tooltip explains — and for
 * the two credential reasons, points at Settings, which is the fix.
 *
 * In *edit* mode the mark renders as a plain styled span with no fetching:
 * live GitLab state has no business re-rendering under the caret, and the
 * text stays ordinarily editable.
 *
 * Fetch rules: nothing is fetched unless `gitlabStatus.configured` (§15's
 * fail-closed extends to UI affordances), and issue state is re-fetched on
 * mount (`cache-and-network`) because embeds are live, never cached — a
 * page must not present stale GitLab state as current (§18).
 */
export function GitLabIssueLinkView({ mark, editor }: MarkViewProps) {
  const project = String(mark.attrs.project ?? '')
  const iid = String(mark.attrs.iid ?? '')
  const editable = editor.isEditable

  const [{ data: statusData }] = useGitLabStatusQuery({ pause: editable })
  const configured = statusData?.gitlabStatus.configured === true
  const [{ data }] = useGitLabIssueQuery({
    variables: { projectId: project, iid: Number(iid) },
    pause: editable || !configured,
    requestPolicy: 'cache-and-network',
  })

  if (editable) {
    return (
      <span className="rw-gitlab-issue-link">
        <MarkViewContent as="span" />
      </span>
    )
  }

  const payload = data?.gitlabIssue
  const issue = payload?.issue ?? null
  // The status answer arriving unconfigured is the same fact as a fetch
  // answering NOT_CONFIGURED — render it identically.
  const unavailable =
    payload?.unavailable ?? (statusData && !configured ? { reason: 'NOT_CONFIGURED' as const } : null)

  if (issue) {
    const stateLabel = issue.state === 'opened' ? 'open' : issue.state
    return (
      <a
        className="rw-gitlab-issue-chip"
        href={issue.webUrl}
        target="_blank"
        rel="noopener noreferrer"
        title={issue.title}
        data-state={issue.state}
      >
        <MarkViewContent as="span" />
        <span className="rw-gitlab-issue-state" data-state={issue.state}>
          {stateLabel}
        </span>
      </a>
    )
  }

  if (unavailable) {
    const copy = describeGitLabUnavailable(unavailable.reason)
    const message = copy.pointsToSettings ? `${copy.summary} (Settings → GitLab)` : copy.summary
    return (
      <span className="rw-gitlab-issue-link rw-gitlab-issue-degraded" title={message}>
        <MarkViewContent as="span" />
        <span className="rw-gitlab-marker" aria-hidden="true">
          !
        </span>
        <span className="rw-visually-hidden">GitLab issue link — {message}</span>
      </span>
    )
  }

  // Status/issue still loading: plain text, no marker — a flash of "broken"
  // would be a lie while the answer is simply in flight.
  return (
    <span className="rw-gitlab-issue-link">
      <MarkViewContent as="span" />
    </span>
  )
}
