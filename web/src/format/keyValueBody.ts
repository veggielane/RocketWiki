/**
 * The `key=value` line format shared by the reserved Markdown fences — the
 * two GitLab embeds (design.md §18) and the `page-list` widget (§22). Lifted
 * out of `gitlab/fenceBody.ts` when the second feature wanted it: the format
 * is not GitLab's, and a helper named after one of its callers is the shape
 * a third caller copies instead of imports.
 *
 * It lives in `format/` because it has no domain — it knows nothing about
 * projects, queries or pages, only about text — and so it can be depended on
 * by a feature folder without dragging one feature into another.
 */

/**
 * Splits a fence body into key/value pairs: one `key=value` per line, split
 * at the first `=`, keys trimmed and case-sensitive, values taken verbatim
 * (trimmed). Blank lines and lines without `=` are ignored; a repeated key
 * keeps the last occurrence. Unknown keys are simply unused — never an
 * error, so future keys degrade gracefully on old clients.
 *
 * Splitting at the FIRST `=` is what lets a value contain one, which the
 * `page-list` fence depends on completely: its whole body is
 * `query = label = "safety"`.
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
