/**
 * The `key=value` fence bodies of the two GitLab embed forms (design.md
 * §18): ` ```gitlab-file ` (project=, path= required; ref= optional) and
 * ` ```gitlab-issues ` (project= required; the rest mirrors
 * GitLabIssueFilterInput). The fence itself stays an ordinary code block to
 * the whole Markdown pipeline — these helpers only interpret its text for
 * rendering and build canonical bodies for the insert dialogs; they never
 * rewrite what an author typed.
 */

/**
 * Splits a fence body into key/value pairs: one `key=value` per line, split
 * at the first `=`, keys trimmed and case-sensitive, values taken verbatim
 * (trimmed). Blank lines and lines without `=` are ignored; a repeated key
 * keeps the last occurrence. Unknown keys are simply unused — never an
 * error, so future keys degrade gracefully on old clients.
 */
export function parseKeyValueBody(body: string): Map<string, string> {
  const entries = new Map<string, string>()
  for (const line of body.split('\n')) {
    const eq = line.indexOf('=')
    if (eq <= 0) continue
    const key = line.slice(0, eq).trim()
    const value = line.slice(eq + 1).trim()
    if (key.length === 0) continue
    entries.set(key, value)
  }
  return entries
}

// --- gitlab-file ---------------------------------------------------------

export interface GitLabFileRef {
  project: string
  path: string
  /** Optional — the server defaults to HEAD. */
  ref?: string
}

export type ParsedFileFence =
  | { ok: true; ref: GitLabFileRef }
  | { ok: false; missing: string[] }

export function parseFileFence(body: string): ParsedFileFence {
  const entries = parseKeyValueBody(body)
  const project = entries.get('project') ?? ''
  const path = entries.get('path') ?? ''
  const missing = [
    ...(project.length === 0 ? ['project'] : []),
    ...(path.length === 0 ? ['path'] : []),
  ]
  if (missing.length > 0) return { ok: false, missing }
  const ref = entries.get('ref')
  return { ok: true, ref: { project, path, ...(ref ? { ref } : {}) } }
}

/** Canonical body the insert dialog writes; parseFileFence(build(x)) === x. */
export function buildFileFenceBody(ref: GitLabFileRef): string {
  const lines = [`project=${ref.project}`, `path=${ref.path}`]
  if (ref.ref && ref.ref.length > 0) lines.push(`ref=${ref.ref}`)
  return lines.join('\n')
}

// --- gitlab-issues -------------------------------------------------------

export interface GitLabIssuesSpec {
  project: string
  state?: string
  labels?: string[]
  search?: string
  milestone?: string
  orderBy?: string
  sort?: string
  first?: number
}

export type ParsedIssuesFence =
  | { ok: true; spec: GitLabIssuesSpec }
  | { ok: false; missing: string[] }

/**
 * Filter values travel verbatim: out-of-vocabulary strings (e.g.
 * `state=banana`) are the *server's* to degrade to unset (§18) — the client
 * must not sharpen or second-guess that vocabulary. The one client-side
 * interpretation is `first`, which must be an Int on the wire: a
 * non-numeric value is treated as unset (server default 20, clamp 1..50).
 */
export function parseIssuesFence(body: string): ParsedIssuesFence {
  const entries = parseKeyValueBody(body)
  const project = entries.get('project') ?? ''
  if (project.length === 0) return { ok: false, missing: ['project'] }

  const spec: GitLabIssuesSpec = { project }
  const state = entries.get('state')
  if (state) spec.state = state
  const labels = entries.get('labels')
  if (labels) {
    const parsed = labels
      .split(',')
      .map((label) => label.trim())
      .filter((label) => label.length > 0)
    if (parsed.length > 0) spec.labels = parsed
  }
  const search = entries.get('search')
  if (search) spec.search = search
  const milestone = entries.get('milestone')
  if (milestone) spec.milestone = milestone
  const orderBy = entries.get('orderBy')
  if (orderBy) spec.orderBy = orderBy
  const sort = entries.get('sort')
  if (sort) spec.sort = sort
  const first = entries.get('first')
  if (first && /^\d+$/.test(first)) spec.first = Number(first)
  return { ok: true, spec }
}

/** Canonical body the insert dialog writes; key order mirrors §18's table. */
export function buildIssuesFenceBody(spec: GitLabIssuesSpec): string {
  const lines = [`project=${spec.project}`]
  if (spec.state) lines.push(`state=${spec.state}`)
  if (spec.labels && spec.labels.length > 0) lines.push(`labels=${spec.labels.join(',')}`)
  if (spec.search) lines.push(`search=${spec.search}`)
  if (spec.milestone) lines.push(`milestone=${spec.milestone}`)
  if (spec.orderBy) lines.push(`orderBy=${spec.orderBy}`)
  if (spec.sort) lines.push(`sort=${spec.sort}`)
  if (spec.first !== undefined) lines.push(`first=${spec.first}`)
  return lines.join('\n')
}
