import { useGitLabIssuesQuery, useGitLabStatusQuery } from '../graphql/generated/graphql'
import type { GitLabIssuesSpec } from './fenceBody'
import { describeGitLabUnavailable } from '../feedback/unavailableCopy'

/**
 * Live preview for a ` ```gitlab-issues ` fence (design.md §18): a compact
 * table of matching issues — title linking out to webUrl, state, labels,
 * assignees. Filter strings travel verbatim (out-of-vocabulary values are
 * the server's to degrade); the degradation vocabulary renders the same
 * typed placeholder as the other embeds, always with the reference.
 *
 * Plain CSS, not MUI — editor-content territory, like the diagram blocks.
 */
export function GitLabIssuesBlock({ spec }: { spec: GitLabIssuesSpec }) {
  const [{ data: statusData }] = useGitLabStatusQuery()
  const configured = statusData?.gitlabStatus.configured === true
  const [{ data }] = useGitLabIssuesQuery({
    variables: {
      projectId: spec.project,
      filter: {
        state: spec.state ?? null,
        labels: spec.labels ?? null,
        search: spec.search ?? null,
        milestone: spec.milestone ?? null,
        orderBy: spec.orderBy ?? null,
        sort: spec.sort ?? null,
      },
      first: spec.first ?? null,
    },
    pause: !configured,
    requestPolicy: 'cache-and-network',
  })

  const payload = data?.gitlabIssues
  const issues = payload?.issues ?? null
  const unavailable =
    payload?.unavailable ?? (statusData && !configured ? { reason: 'NOT_CONFIGURED' as const } : null)

  if (unavailable) {
    const copy = describeGitLabUnavailable(unavailable.reason)
    return (
      <div className="rw-gitlab-placeholder" role="note">
        <div className="rw-gitlab-placeholder-reason">
          GitLab issues unavailable — {copy.summary}
          {copy.pointsToSettings && (
            <>
              {' '}
              <a href="/settings">Open Settings</a>
            </>
          )}
        </div>
        <code className="rw-gitlab-placeholder-ref">{describeSpec(spec)}</code>
      </div>
    )
  }

  if (!issues) {
    return (
      <div className="rw-gitlab-placeholder" role="note">
        <div className="rw-diagram-hint">Loading GitLab issues…</div>
        <code className="rw-gitlab-placeholder-ref">{describeSpec(spec)}</code>
      </div>
    )
  }

  if (issues.length === 0) {
    return (
      <div className="rw-gitlab-issues">
        <div className="rw-gitlab-issues-header">{describeSpec(spec)}</div>
        <div className="rw-diagram-hint">No matching issues.</div>
      </div>
    )
  }

  return (
    <div className="rw-gitlab-issues">
      <div className="rw-gitlab-issues-header">{describeSpec(spec)}</div>
      <table>
        <thead>
          <tr>
            <th scope="col">Issue</th>
            <th scope="col">State</th>
            <th scope="col">Labels</th>
            <th scope="col">Assignees</th>
          </tr>
        </thead>
        <tbody>
          {issues.map((issue) => (
            <tr key={`${issue.project}#${issue.iid}`}>
              <td>
                <a href={issue.webUrl} target="_blank" rel="noopener noreferrer">
                  #{issue.iid} {issue.title}
                </a>
              </td>
              <td>
                <span className="rw-gitlab-issue-state" data-state={issue.state}>
                  {issue.state === 'opened' ? 'open' : issue.state}
                </span>
              </td>
              <td>{issue.labels.join(', ')}</td>
              <td>{issue.assigneeNames.join(', ')}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/** Compact human form of the reference, shown in headers and placeholders. */
function describeSpec(spec: GitLabIssuesSpec): string {
  const parts = [`project=${spec.project}`]
  if (spec.state) parts.push(`state=${spec.state}`)
  if (spec.labels && spec.labels.length > 0) parts.push(`labels=${spec.labels.join(',')}`)
  if (spec.search) parts.push(`search=${spec.search}`)
  if (spec.milestone) parts.push(`milestone=${spec.milestone}`)
  return parts.join(' ')
}
