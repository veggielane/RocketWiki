/**
 * Deterministic anchor ids for headings, so a search result (§9) can
 * deep-link to the exact section that matched, not just the page.
 *
 * The id is derived from the heading's *path* (its own text plus every
 * ancestor heading's text, same "breadcrumb" shape as
 * `SearchHit.headingPath`) rather than a positional index — editing
 * anything above a heading, or adding/removing unrelated headings
 * elsewhere, never changes that heading's path, so the anchor is stable
 * across edits. Two exceptions, both inherent to path-based addressing
 * rather than bugs:
 *   - Renaming a heading, or moving it under a different parent, changes
 *     its path and therefore its anchor — expected, since the anchor *is*
 *     the path.
 *   - Two headings that are genuinely identical in text *and* position in
 *     the heading hierarchy (e.g. two top-level "## Overview" sections)
 *     are disambiguated by *occurrence order among exact duplicates only*
 *     (first gets the bare slug, second gets `-2`, …) — inserting a new
 *     duplicate-path heading *before* existing ones shifts their ordinals.
 *     That's unavoidable without abandoning path-based addressing
 *     entirely (e.g. random ids), which would fail the "deterministic"
 *     requirement instead. Genuinely rare in practice.
 *
 * This same algorithm is the contract `SearchHit.anchorId` needs the
 * backend to replicate exactly (see schema.placeholder.graphql) — the
 * frontend defines it here first since there's no backend yet to author
 * it against; whoever implements the real resolver should port this file
 * exactly, not re-derive it.
 */

export interface HeadingInfo {
  level: number
  text: string
}

/** Slugify one heading's own text — lowercase, alphanumeric + hyphens, collapsed. */
function slugifySegment(text: string): string {
  const slug = text
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9\s-]/g, '')
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '')
  return slug.length > 0 ? slug : 'section'
}

/** One heading's path: ancestor headings' text (by level nesting), ending with its own. */
export function headingPath(text: string, level: number, stack: HeadingInfo[]): string[] {
  while (stack.length > 0 && stack[stack.length - 1]!.level >= level) {
    stack.pop()
  }
  stack.push({ level, text })
  return stack.map((h) => h.text)
}

/**
 * Walks all headings on a page, in document order, and returns the
 * breadcrumb path for each — ancestor texts by heading-level nesting,
 * ending with the heading's own text (e.g. H1 "Intro" > H2 "Setup" > H3
 * "Windows" => `["Intro", "Setup", "Windows"]`).
 */
export function computeHeadingPaths(headings: HeadingInfo[]): string[][] {
  const stack: HeadingInfo[] = []
  return headings.map((h) => headingPath(h.text, h.level, stack))
}

export function slugifyPath(path: string[]): string {
  return path.map(slugifySegment).join('--')
}

/**
 * The public entry point: headings in document order in, one anchor id
 * per heading out, with duplicate-path disambiguation applied.
 */
export function computeHeadingAnchors(headings: HeadingInfo[]): string[] {
  const paths = computeHeadingPaths(headings)
  const seen = new Map<string, number>()
  return paths.map((path) => {
    const base = slugifyPath(path)
    const occurrence = (seen.get(base) ?? 0) + 1
    seen.set(base, occurrence)
    return occurrence === 1 ? base : `${base}-${occurrence}`
  })
}
