import { useGitLabFileQuery, useGitLabStatusQuery } from '../graphql/generated/graphql'
import type { GitLabFileRef } from './fenceBody'
import { describeGitLabUnavailable } from '../feedback/unavailableCopy'
// The one size formatter (decimal KB/MB): a hand-rolled binary KiB version
// here once made the same file show a different size than the attachment
// list — same number, two renderings.
import { formatBytes } from '../attachments/formatBytes'

/**
 * Live preview for a ` ```gitlab-file ` fence (design.md §18): header with
 * fileName / ref / size / link out, body = the file content in a scrollable
 * <pre>. The content is third-party text and is rendered exclusively as
 * *text* (React text node inside <pre>) — never markup, never
 * dangerouslySetInnerHTML; there is deliberately no syntax highlighting in
 * v1. A `contentIssue` (TOO_LARGE / NOT_TEXT) still renders the metadata
 * header with a "content withheld" body; `unavailable` renders the typed
 * placeholder WITH the reference, which is already the page's own Markdown
 * and so discloses nothing new.
 *
 * Plain CSS classes, not MUI — this renders inside the editor content
 * (TipTap NodeView territory), same convention as the diagram blocks.
 */
export function GitLabFileBlock({ fileRef }: { fileRef: GitLabFileRef }) {
  const [{ data: statusData }] = useGitLabStatusQuery()
  const status = statusData?.gitlabStatus
  const configured = status?.configured === true
  const [{ data }] = useGitLabFileQuery({
    variables: { projectId: fileRef.project, path: fileRef.path, ref: fileRef.ref ?? null },
    pause: !configured,
    requestPolicy: 'cache-and-network',
  })

  const payload = data?.gitlabFile
  const file = payload?.file ?? null
  const unavailable =
    payload?.unavailable ?? (statusData && !configured ? { reason: 'NOT_CONFIGURED' as const } : null)

  if (unavailable) {
    const copy = describeGitLabUnavailable(unavailable.reason)
    return (
      <div className="rw-gitlab-placeholder" role="note">
        <div className="rw-gitlab-placeholder-reason">
          GitLab file unavailable — {copy.summary}
          {copy.pointsToSettings && (
            <>
              {' '}
              <a href="/settings">Open Settings</a>
            </>
          )}
        </div>
        <GitLabFileReference fileRef={fileRef} />
      </div>
    )
  }

  if (!file) {
    return (
      <div className="rw-gitlab-placeholder" role="note">
        <div className="rw-diagram-hint">Loading GitLab file…</div>
        <GitLabFileReference fileRef={fileRef} />
      </div>
    )
  }

  const webUrl = fileWebUrl(status?.baseUrl ?? null, file.project, file.filePath, file.ref)
  return (
    <div className="rw-gitlab-file">
      <div className="rw-gitlab-file-header">
        <span className="rw-gitlab-file-name">{file.fileName ?? file.filePath}</span>
        <span className="rw-gitlab-file-meta">
          {file.ref ?? 'HEAD'} · {formatBytes(file.sizeBytes)}
        </span>
        {webUrl && (
          <a className="rw-gitlab-file-link" href={webUrl} target="_blank" rel="noopener noreferrer">
            Open in GitLab
          </a>
        )}
      </div>
      {file.contentIssue !== null && (
        <div className="rw-gitlab-file-withheld">
          {file.contentIssue === 'TOO_LARGE'
            ? 'Content withheld: too large to embed.'
            : 'Content withheld: not a text file.'}
        </div>
      )}
      {file.contentIssue === null && (
        <pre className="rw-code-block rw-gitlab-file-content" tabIndex={0}>
          {file.content ?? ''}
        </pre>
      )}
    </div>
  )
}

/** §18: the placeholder shows the reference — it's already the page's own Markdown. */
function GitLabFileReference({ fileRef }: { fileRef: GitLabFileRef }) {
  return (
    <code className="rw-gitlab-placeholder-ref">
      {fileRef.project}:{fileRef.path}
      {fileRef.ref ? `@${fileRef.ref}` : ''}
    </code>
  )
}

/**
 * Best-effort link out. Only possible when the project is a namespaced
 * path — a numeric project id has no stable web URL we could build without
 * asking GitLab, so the link is simply omitted there.
 */
function fileWebUrl(baseUrl: string | null, project: string, path: string, ref: string | null): string | null {
  if (!baseUrl || /^\d+$/.test(project)) return null
  const encodedPath = path.split('/').map(encodeURIComponent).join('/')
  return `${baseUrl.replace(/\/$/, '')}/${project}/-/blob/${encodeURIComponent(ref ?? 'HEAD')}/${encodedPath}`
}

