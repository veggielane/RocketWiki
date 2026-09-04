import type { FormFieldType } from '../graphql/generated/graphql'

/**
 * The `where =` line of a `form-list` fence.
 *
 * **Why this one is evaluated on the client, when RQL is not.** §22.3 keeps page
 * querying server-side because an RQL predicate selects from every page that
 * exists, so the filter itself is the disclosure — run it client-side and you
 * would have had to ship the rows first. Here the opposite is true: the block has
 * already been handed one collection on one page, permission-filtered by
 * the server, and it is narrowing rows it is already
 * entitled to read. Filtering them locally reveals nothing it did not already
 * have, and moving it to the server would buy nothing but a round trip.
 *
 * That reasoning holds only while the fetch stays scoped to one page. A
 * cross-page `form-list` would select from records it has not been given, and the
 * filter would have to move server-side with it.
 *
 * **Nothing is ever silently ignored.** §22.3's rule, and the bug this replaces:
 * an ignored predicate is worse than a refused one, because the author believes
 * a filter applied and reads the results as if it had. Unsupported syntax and
 * unknown field names both come back as errors naming the fragment.
 */

export type ComparisonOperator = '=' | '!=' | '>' | '>=' | '<' | '<='

export interface EntryCondition {
  field: string
  operator: ComparisonOperator | 'IN'
  /** One value for a comparison; the list for `IN`. */
  values: string[]
}

export type EntryFilterParse =
  | { ok: true; conditions: EntryCondition[] }
  | { ok: false; message: string }

/** Longest first, so `>=` is never read as `>` followed by a stray `=`. */
const OPERATORS: ComparisonOperator[] = ['>=', '<=', '!=', '=', '>', '<']

function unquote(value: string): string {
  const trimmed = value.trim()
  const quoted = /^"(.*)"$/.exec(trimmed) ?? /^'(.*)'$/.exec(trimmed)
  return quoted ? quoted[1]! : trimmed
}

/**
 * Splits on `AND` only. `OR` is deliberately unsupported rather than
 * half-implemented: mixing the two needs precedence and parentheses, and a
 * filter that guessed at precedence would silently mean something other than
 * what it reads like. It is refused by name below.
 */
export function parseEntryFilter(where: string | undefined): EntryFilterParse {
  const text = (where ?? '').trim()
  if (text.length === 0) {
    return { ok: true, conditions: [] }
  }

  if (/\bOR\b/i.test(text)) {
    return { ok: false, message: 'OR is not supported in a form-list filter — use one condition per line of AND.' }
  }
  if (text.includes('(') && !/\bIN\b/i.test(text)) {
    return { ok: false, message: 'Parentheses are not supported in a form-list filter.' }
  }

  const conditions: EntryCondition[] = []
  for (const clause of text.split(/\s+AND\s+/i)) {
    const fragment = clause.trim()
    if (fragment.length === 0) continue

    const inMatch = /^(.+?)\s+IN\s*\(([^)]*)\)$/i.exec(fragment)
    if (inMatch) {
      const values = inMatch[2]!.split(',').map(unquote).filter((v) => v.length > 0)
      if (values.length === 0) {
        return { ok: false, message: `"${fragment}" lists no values.` }
      }
      conditions.push({ field: inMatch[1]!.trim(), operator: 'IN', values })
      continue
    }

    const operator = OPERATORS.find((op) => fragment.includes(op))
    if (!operator) {
      return {
        ok: false,
        message: `Could not read "${fragment}". Expected something like 'severity = high' or 'severity IN (high, medium)'.`,
      }
    }

    const at = fragment.indexOf(operator)
    const field = fragment.slice(0, at).trim()
    const value = unquote(fragment.slice(at + operator.length))
    if (field.length === 0 || value.length === 0) {
      return { ok: false, message: `Could not read "${fragment}".` }
    }

    conditions.push({ field, operator, values: [value] })
  }

  return { ok: true, conditions }
}

/**
 * Names any condition referring to a field the form does not declare.
 *
 * A typo would otherwise match nothing and present as "no records yet", which is
 * the same class of lie as an ignored predicate — the author reads an empty
 * table as a fact about the data rather than about their filter.
 */
export function unknownFields(conditions: EntryCondition[], declared: readonly string[]): string[] {
  const known = new Set(declared.map((f) => f.toLowerCase()))
  return conditions.map((c) => c.field).filter((f) => !known.has(f.toLowerCase()))
}

function compare(left: string, right: string, type: FormFieldType): number {
  if (type === 'NUMBER') {
    const a = Number(left)
    const b = Number(right)
    // A non-numeric value in a number field sorts as unequal rather than
    // throwing: the data is author-supplied and may predate the field's type.
    if (Number.isNaN(a) || Number.isNaN(b)) return left === right ? 0 : NaN
    return a - b
  }
  // ISO dates compare correctly as strings, which is why the date field stores
  // them that way rather than as a parsed instant.
  return left < right ? -1 : left > right ? 1 : 0
}

export function entryMatches(
  fields: Record<string, unknown>,
  conditions: readonly EntryCondition[],
  typeOf: (field: string) => FormFieldType,
): boolean {
  return conditions.every((condition) => {
    const raw = fields[condition.field]
    const value = raw === null || raw === undefined ? '' : String(raw)

    if (condition.operator === 'IN') {
      return condition.values.some((v) => v.toLowerCase() === value.toLowerCase())
    }

    const target = condition.values[0]!
    if (condition.operator === '=') return value.toLowerCase() === target.toLowerCase()
    if (condition.operator === '!=') return value.toLowerCase() !== target.toLowerCase()

    const order = compare(value, target, typeOf(condition.field))
    if (Number.isNaN(order)) return false
    switch (condition.operator) {
      case '>':
        return order > 0
      case '>=':
        return order >= 0
      case '<':
        return order < 0
      case '<=':
        return order <= 0
    }
  })
}
