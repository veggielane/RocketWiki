import type { FormFieldType } from '../graphql/generated/graphql'

/**
 * Builds the two form fence bodies for the insert dialog.
 *
 * Building only — never rewriting what an author typed. Once a fence is on the
 * page it is theirs, and the parsers read it as written; these helpers exist so
 * that the FIRST one someone inserts is valid, because the field syntax is the
 * part nobody guesses correctly.
 */

export interface FormFieldDraft {
  name: string
  type: FormFieldType
  required: boolean
  /** Comma-separated, as typed. Only meaningful for a select. */
  options: string
}

export interface FormDefinitionDraft {
  collection: string
  fields: FormFieldDraft[]
}

/** Lower-case, matching what the server stores — so the definition a dialog writes
 *  and the collection its records land in are spelled the same from the start. */
function typeToken(type: FormFieldType): string {
  return type.toLowerCase()
}

export function buildFormDefinitionFenceBody(draft: FormDefinitionDraft): string {
  const lines = [`collection = ${draft.collection.trim()}`]
  for (const field of draft.fields) {
    const name = field.name.trim()
    if (name.length === 0) continue

    let token = typeToken(field.type)
    if (field.type === 'SELECT') {
      const options = field.options
        .split(',')
        .map((o) => o.trim())
        .filter((o) => o.length > 0)
      token += `(${options.join(', ')})`
    }

    lines.push(`field = ${name}: ${token}${field.required ? ', required' : ''}`)
  }
  return lines.join('\n')
}

export interface FormListDraft {
  collection: string
  /** Empty means every field the definition declares. */
  columns: string[]
  where: string
}

export function buildFormListFenceBody(draft: FormListDraft): string {
  const lines = [`collection = ${draft.collection.trim()}`]
  if (draft.columns.length > 0) {
    lines.push(`columns = ${draft.columns.join(', ')}`)
  }
  // Omitted rather than written empty: `where =` with nothing after it parses as
  // no filter, but it reads like an unfinished thought in the author's page.
  if (draft.where.trim().length > 0) {
    lines.push(`where = ${draft.where.trim()}`)
  }
  return lines.join('\n')
}
