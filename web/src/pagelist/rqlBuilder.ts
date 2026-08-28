/**
 * The insert dialog's simple builder, as a pure function: chosen labels, a
 * combine mode, chosen spaces and a sort choice in — an RQL string out
 * (design.md §22.1).
 *
 * A *printer*, deliberately not a parser and not a validator. §22 publishes
 * the AST as output only, precisely so a client-built tree cannot become a
 * second door into the compiler; the builder's whole contract is the one
 * §22.6 states — "it builds, prints through the canonical printer, stores the
 * string, and reparses it". So this produces text, `parseRql` canonicalizes
 * it, and the canonical form is what gets stored.
 */

/** ORDER BY's field set is `title`, `created`, `updated` only (§22.2). */
export type PageListSort = 'default' | 'updated-desc' | 'updated-asc' | 'created-desc' | 'created-asc' | 'title-asc'

export interface PageListBuilderState {
  labels: string[]
  /**
   * How the chosen labels combine. `any` prints an `IN` list rather than an
   * `OR` chain: they are the same predicate, but `IN` needs no parentheses to
   * sit beside the space clause, so nothing here has to reason about
   * precedence.
   */
  labelMode: 'all' | 'any'
  spaceKeys: string[]
  sort: PageListSort
}

const SORT_CLAUSE: Record<PageListSort, string | null> = {
  // §22.2: the default IS `updated DESC`, applied at execution, and the
  // parsed AST keeps ORDER BY empty — "a query that merely passes through the
  // builder does not grow a clause it never had". So the default prints
  // nothing rather than printing the default.
  default: null,
  'updated-desc': 'ORDER BY updated DESC',
  'updated-asc': 'ORDER BY updated ASC',
  'created-desc': 'ORDER BY created DESC',
  'created-asc': 'ORDER BY created ASC',
  'title-asc': 'ORDER BY title ASC',
}

/**
 * Values are always quoted and escaped, mirroring the canonical printer's own
 * rule (§22.6) — "so no value can be re-lexed as syntax". A label that spells
 * a keyword (`label = "in"`) or contains a quote is therefore safe by
 * construction rather than by the caller remembering.
 *
 * The escape set is RQL's closed one: `\" \\ \n \r \t`. A value containing
 * anything else is passed through and left for the server to reject, which is
 * the right division of labour — the client does not own the grammar.
 */
export function quoteRqlValue(value: string): string {
  const escaped = value
    .replace(/\\/g, '\\\\')
    .replace(/"/g, '\\"')
    .replace(/\n/g, '\\n')
    .replace(/\r/g, '\\r')
    .replace(/\t/g, '\\t')
  return `"${escaped}"`
}

/**
 * Prints the builder state. Returns `''` when nothing is selected: an
 * ORDER BY on its own is not a query in §22.1's grammar, so there is no
 * honest string to emit and the dialog keeps Insert disabled instead.
 */
export function buildRqlFromBuilder(state: PageListBuilderState): string {
  const clauses: string[] = []

  if (state.labels.length === 1) {
    clauses.push(`label = ${quoteRqlValue(state.labels[0]!)}`)
  } else if (state.labels.length > 1) {
    clauses.push(
      state.labelMode === 'any'
        ? `label IN (${state.labels.map(quoteRqlValue).join(', ')})`
        : state.labels.map((label) => `label = ${quoteRqlValue(label)}`).join(' AND '),
    )
  }

  if (state.spaceKeys.length === 1) {
    clauses.push(`space = ${quoteRqlValue(state.spaceKeys[0]!)}`)
  } else if (state.spaceKeys.length > 1) {
    clauses.push(`space IN (${state.spaceKeys.map(quoteRqlValue).join(', ')})`)
  }

  if (clauses.length === 0) return ''

  const sort = SORT_CLAUSE[state.sort]
  return sort === null ? clauses.join(' AND ') : `${clauses.join(' AND ')} ${sort}`
}
