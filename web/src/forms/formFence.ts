import { parseKeyValueBody } from '../format/keyValueBody'

/**
 * The `form-definition` and `form-list` fence bodies, as the SPA needs them.
 *
 * Only enough to know WHICH form is meant and which columns to draw. The fields
 * themselves are never parsed here — the server does that and hands back a parsed
 * definition, so rendering and validation cannot drift into two opinions about what a
 * form is. That is the same split `page-list` uses, where the SPA does not parse RQL.
 */

export interface FormFenceSpec {
  collection: string
  /** Empty means every field the definition declares, in its order. */
  columns: string[]
  /** The raw `where =` text. Parsed by entryFilter.ts, not here — this module
   *  only splits the fence body. */
  where: string
}

export type FormFenceParse =
  | { ok: true; spec: FormFenceSpec }
  | { ok: false; missing: string[] }

export function parseFormFence(body: string): FormFenceParse {
  const entries = parseKeyValueBody(body)
  const collection = entries.get('collection')?.trim() ?? ''
  if (collection.length === 0) {
    // Reported as missing rather than as an error, so a half-typed fence in the editor
    // reads as incomplete instead of broken — the same state `page-list` shows.
    return { ok: false, missing: ['collection'] }
  }

  const columns = (entries.get('columns') ?? '')
    .split(',')
    .map((c) => c.trim())
    .filter((c) => c.length > 0)

  return { ok: true, spec: { collection, columns, where: entries.get('where')?.trim() ?? '' } }
}
