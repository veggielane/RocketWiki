import type { AskWikiQuery } from '../graphql/generated/graphql'

export type AskCitation = AskWikiQuery['askWiki']['citations'][number]

export type AnswerSegment =
  | { kind: 'text'; text: string }
  /** A resolved `[Sn]` marker — `n` is the 1-based marker number, `citation` the entry it indexes. */
  | { kind: 'citation'; n: number; citation: AskCitation }

/**
 * Splits an answer into plain-text runs and resolved `[Sn]` citation
 * markers (design.md §9.5: `n` is 1-based into the citations array, in
 * order of first appearance — repeats and out-of-order references are
 * legal, an answer may cite S2 before S1 or S1 twice).
 *
 * The answer is model output and therefore untrusted text. This transform
 * is the ONLY interpretation applied to it: everything between markers
 * stays an opaque string for React to render as text nodes (v1 tradeoff:
 * no Markdown rendering of the answer — a model that emits HTML or
 * Markdown shows it literally rather than gaining any power over the
 * page). A marker whose number has no matching citation — the server
 * strips fabricated markers, but this layer doesn't assume that — falls
 * through as plain text rather than a dead link.
 */
export function segmentAnswer(answer: string, citations: readonly AskCitation[]): AnswerSegment[] {
  const segments: AnswerSegment[] = []
  let cursor = 0
  // `\d{1,3}`: markers are small positional indexes; refusing to parse
  // absurd widths keeps "[S99999999999999999999]" safely as text.
  const marker = /\[S(\d{1,3})\]/g
  for (const match of answer.matchAll(marker)) {
    const n = Number.parseInt(match[1], 10)
    const citation = n >= 1 ? citations[n - 1] : undefined
    if (citation === undefined) continue // unmatched marker: stays inside the surrounding text run
    if (match.index > cursor) {
      segments.push({ kind: 'text', text: answer.slice(cursor, match.index) })
    }
    segments.push({ kind: 'citation', n, citation })
    cursor = match.index + match[0].length
  }
  if (cursor < answer.length) {
    segments.push({ kind: 'text', text: answer.slice(cursor) })
  }
  return segments
}

/** The same deep-link contract search hits use (§9.2 anchors). */
export function citationHref(citation: AskCitation): string {
  return `/pages/${citation.pageId}#${citation.anchorId}`
}
