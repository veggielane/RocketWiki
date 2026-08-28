/**
 * The ` ```page-list ` fence body (design.md §22): an RQL query and an
 * optional display limit, as `key=value` lines.
 *
 *     query = label = "safety" AND space IN ("ENG", "OPS")
 *     limit = 20
 *
 * The fence stays an ordinary `codeBlock` with `attrs.language` to the whole
 * Markdown pipeline — no bespoke node, no `fromMarkdown`/`toMarkdown` change —
 * which is what makes the byte-exact round trip (§4), sync inertness (§12)
 * and importer safety (§13) free rather than something this feature has to
 * re-earn. `drawio` needed a bespoke node only because its payload is opaque
 * base64; nothing here is opaque.
 *
 * These helpers interpret the fence's text for rendering and build canonical
 * bodies for the insert dialog. They never rewrite what an author typed.
 */
import { parseKeyValueBody } from '../format/keyValueBody'

export interface PageListSpec {
  /**
   * The RQL string, verbatim. **Never validated here.** §22.4 is explicit
   * that RQL validation is the server's — the vocabulary, the closed field
   * set, and the refusals all live in `Rql.Parse` — and a client-side
   * second opinion would be a second door into the grammar. An unparseable
   * query reaches `pageQuery`, comes back as positioned `errors`, and the
   * widget renders them.
   */
  query: string
  /**
   * How many rows the widget draws. Optional; absent means the server's own
   * page size. Non-numeric text is treated as unset rather than as an error,
   * the same call the GitLab `first=` key makes: `first` must be an Int on
   * the wire, so there is nothing else to send.
   */
  limit?: number
}

export type ParsedPageListFence = { ok: true; spec: PageListSpec } | { ok: false; missing: string[] }

export function parsePageListFence(body: string): ParsedPageListFence {
  const entries = parseKeyValueBody(body)
  const query = entries.get('query') ?? ''
  if (query.length === 0) return { ok: false, missing: ['query'] }

  const spec: PageListSpec = { query }
  const limit = entries.get('limit')
  if (limit && /^\d+$/.test(limit) && Number(limit) > 0) spec.limit = Number(limit)
  return { ok: true, spec }
}

/** Canonical body the insert dialog writes; parsePageListFence(build(x)) === x. */
export function buildPageListFenceBody(spec: PageListSpec): string {
  const lines = [`query = ${spec.query}`]
  if (spec.limit !== undefined) lines.push(`limit = ${spec.limit}`)
  return lines.join('\n')
}
