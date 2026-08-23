/**
 * The `gitlab-issue://{project}/{iid}` scheme (design.md §18): `{project}`
 * is a numeric id or namespaced path, `{iid}` the final numeric segment.
 * The stored form is scheme-only and host-free — a pasted GitLab URL means
 * "this instance's GitLab", not "that hostname", so it survives the
 * low→high crossing and GitLab migrations.
 *
 * Both halves stay raw strings end to end: the round-trip rule (§4) is
 * byte-identity, so nothing here may ever re-format what the author wrote
 * (e.g. `Number()`-ing an iid would silently strip a leading zero).
 */

export interface GitLabIssueRef {
  /** Numeric project id ("142") or namespaced path ("group/subgroup/proj"), verbatim. */
  project: string
  /** Issue iid digits, verbatim. */
  iid: string
}

export const GITLAB_ISSUE_SCHEME = 'gitlab-issue://'

/** Everything up to the final `/` is the project; the final segment must be all digits. */
const TARGET_PATTERN = /^(.+)\/(\d+)$/

/**
 * Parses a full `gitlab-issue://…` href. Returns null for anything that
 * doesn't match the scheme's shape (wrong scheme, non-numeric final
 * segment, empty project) — callers keep malformed hrefs as ordinary links
 * so the text still round-trips byte-identically instead of being "fixed".
 */
export function parseGitLabIssueTarget(href: string): GitLabIssueRef | null {
  if (!href.startsWith(GITLAB_ISSUE_SCHEME)) return null
  const rest = href.slice(GITLAB_ISSUE_SCHEME.length)
  const match = TARGET_PATTERN.exec(rest)
  if (!match) return null
  return { project: match[1]!, iid: match[2]! }
}

export function formatGitLabIssueTarget(ref: GitLabIssueRef): string {
  return `${GITLAB_ISSUE_SCHEME}${ref.project}/${ref.iid}`
}

/**
 * Converts a pasted GitLab issue *URL* into the scheme form — the explicit
 * affordance §18 permits (bare-URL auto-detection was rejected; this only
 * runs when the user pastes into the insert dialog). The host is stripped
 * by construction: only the path is read.
 *
 * Understands both URL shapes GitLab has used:
 *   https://host/group/sub/proj/-/issues/123   (modern)
 *   https://host/group/proj/issues/123         (legacy)
 */
export function parseGitLabIssueUrl(url: string): GitLabIssueRef | null {
  let path: string
  try {
    path = new URL(url).pathname
  } catch {
    return null
  }
  const match = /^\/(.+?)(?:\/-)?\/issues\/(\d+)\/?$/.exec(path)
  if (!match) return null
  const project = match[1]!
  // A project path never contains the `/-/` separator itself.
  if (project.length === 0 || project.includes('/-/')) return null
  return { project, iid: match[2]! }
}
