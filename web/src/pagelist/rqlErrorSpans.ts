/**
 * Turning RQL's positioned errors into something a reader can see.
 *
 * §22.5 collects vocabulary errors *with offsets* specifically "so an editor
 * underlines them at once", and §22.6 keeps them in the payload rather than
 * as a GraphQL error so a widget can place them. Both consumers — the insert
 * dialog, which underlines while you type, and the rendered widget, which
 * quotes the offending text back — go through here so neither invents its own
 * arithmetic over the same offsets.
 *
 * Offsets are treated as untrusted: they index a string this client did not
 * measure, so every slice is clamped. A bad offset degrades to "no highlight",
 * never to a crash or to text from somewhere else in the query.
 */

/** The minimum of `RqlError` this module needs — not the generated type, so a test can build one. */
export interface PositionedError {
  offset: number
  length: number
}

/** The exact text an error points at, or `''` if the span is empty or out of range. */
export function offendingText(query: string, error: PositionedError): string {
  const start = clamp(error.offset, 0, query.length)
  const end = clamp(error.offset + error.length, start, query.length)
  return query.slice(start, end)
}

export interface QuerySpan {
  text: string
  /** True for the stretches an error points at — the dialog marks these. */
  isError: boolean
}

/**
 * Splits the query into consecutive spans, flagging every stretch covered by
 * at least one error. Overlapping and out-of-order errors are handled by
 * merging their ranges rather than by trusting the server to send them
 * disjoint and sorted: the highlight is a display detail and must not depend
 * on a guarantee §22 never makes.
 *
 * Always covers the whole string, so joining the span texts reproduces the
 * query exactly — the property the dialog relies on to render marked and
 * unmarked text in one line without losing a character.
 */
export function querySpans(query: string, errors: readonly PositionedError[]): QuerySpan[] {
  const ranges = errors
    .map((e) => {
      const start = clamp(e.offset, 0, query.length)
      return { start, end: clamp(e.offset + e.length, start, query.length) }
    })
    .filter((r) => r.end > r.start)
    .sort((a, b) => a.start - b.start)

  const merged: { start: number; end: number }[] = []
  for (const range of ranges) {
    const last = merged[merged.length - 1]
    if (last && range.start <= last.end) last.end = Math.max(last.end, range.end)
    else merged.push({ ...range })
  }

  if (merged.length === 0) return query.length > 0 ? [{ text: query, isError: false }] : []

  const spans: QuerySpan[] = []
  let cursor = 0
  for (const range of merged) {
    if (range.start > cursor) spans.push({ text: query.slice(cursor, range.start), isError: false })
    spans.push({ text: query.slice(range.start, range.end), isError: true })
    cursor = range.end
  }
  if (cursor < query.length) spans.push({ text: query.slice(cursor), isError: false })
  return spans
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max)
}
