import { diffLines, diffWordsWithSpace } from 'diff'

/**
 * Line-based Markdown diff for the StaleRevision merge flow (design.md §5:
 * "view their changes / overwrite / copy my text"). Orientation is fixed as
 * your draft → their latest revision: a `removed` line exists only in YOUR
 * draft, an `added` line only in THEIR revision.
 *
 * Built on the `diff` package (jsdiff): line diff via `diffLines`, plus
 * word-level highlighting *within* a changed line when a removed line pairs
 * up with an added one and the two are similar enough for the marks to mean
 * anything. Both sources are raw Markdown from the page-content trust
 * boundary — callers must render the result as text, never as HTML.
 */

export type DiffLineKind = 'unchanged' | 'added' | 'removed'

export interface DiffSegment {
  text: string
  /** True when this run is what actually differs between the paired lines. */
  changed: boolean
}

export interface DiffLine {
  kind: DiffLineKind
  /** The full line, without its trailing newline. */
  text: string
  /**
   * Word-level segmentation of a changed line, present only when this
   * removed/added line was paired with a counterpart and the pair passed
   * the similarity guard. Concatenating `segments[].text` equals `text`.
   */
  segments: DiffSegment[] | null
}

/**
 * Below this ratio of unchanged characters (relative to the longer line of
 * the pair) the lines are effectively rewrites, and word marks would just
 * paint the whole line — noise, not evidence. Show them as plain
 * removed/added lines instead.
 */
const WORD_MARK_SIMILARITY_THRESHOLD = 0.3

/** Split a jsdiff change value into lines, dropping the trailing-newline artifact. */
function toLines(value: string): string[] {
  if (value === '') return []
  const lines = value.split('\n')
  if (lines[lines.length - 1] === '') lines.pop()
  return lines
}

/** Word-level segments for one paired line, keeping only the given side. */
function segmentPair(yourLine: string, theirLine: string): { yours: DiffSegment[]; theirs: DiffSegment[] } | null {
  const parts = diffWordsWithSpace(yourLine, theirLine)
  const commonChars = parts.filter((p) => !p.added && !p.removed).reduce((n, p) => n + p.value.length, 0)
  const longer = Math.max(yourLine.length, theirLine.length)
  if (longer === 0 || commonChars / longer < WORD_MARK_SIMILARITY_THRESHOLD) return null
  return {
    yours: parts.filter((p) => !p.added).map((p) => ({ text: p.value, changed: p.removed })),
    theirs: parts.filter((p) => !p.removed).map((p) => ({ text: p.value, changed: p.added })),
  }
}

export function computeStaleDiff(yourDraft: string, theirContent: string): DiffLine[] {
  const changes = diffLines(yourDraft, theirContent)
  const out: DiffLine[] = []

  for (let i = 0; i < changes.length; i++) {
    const change = changes[i]
    const lines = toLines(change.value)

    if (!change.added && !change.removed) {
      for (const text of lines) out.push({ kind: 'unchanged', text, segments: null })
      continue
    }

    // jsdiff emits a replaced region as a removed block immediately followed
    // by an added block. Pair them line-by-line for word-level marks.
    const next = changes[i + 1]
    if (change.removed && next?.added) {
      const theirLines = toLines(next.value)
      const paired = new Map<number, { yours: DiffSegment[]; theirs: DiffSegment[] }>()
      for (let j = 0; j < Math.min(lines.length, theirLines.length); j++) {
        const segments = segmentPair(lines[j], theirLines[j])
        if (segments) paired.set(j, segments)
      }
      lines.forEach((text, j) => out.push({ kind: 'removed', text, segments: paired.get(j)?.yours ?? null }))
      theirLines.forEach((text, j) => out.push({ kind: 'added', text, segments: paired.get(j)?.theirs ?? null }))
      i++ // consumed the added block
      continue
    }

    const kind: DiffLineKind = change.removed ? 'removed' : 'added'
    for (const text of lines) out.push({ kind, text, segments: null })
  }

  return out
}

/** True when the diff contains no added or removed lines (identical documents). */
export function isUnchanged(lines: DiffLine[]): boolean {
  return lines.every((line) => line.kind === 'unchanged')
}
