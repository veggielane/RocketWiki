/**
 * The URL slug a new page gets from its title.
 *
 * Deliberately NOT the heading-anchor slugifier in editor/headingAnchors.ts, even
 * though the character rules are nearly identical. That one is half of a byte-exact
 * cross-language contract — the converted-markdown and heading-anchor corpora pin it,
 * and the server computes the same anchors independently — so reusing it here would
 * mean any future change to how page slugs read (a length cap, different handling of
 * accented characters, whatever) would silently break every existing deep link into a
 * heading. Two small implementations that are free to diverge beat one that cannot
 * move.
 */

/** Longest slug we will derive. Long enough to stay readable, short enough for a URL. */
const MAX_LENGTH = 80

export function slugifyTitle(title: string): string {
  const slug = title
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9\s-]/g, '')
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '')

  if (slug.length <= MAX_LENGTH) {
    return slug
  }

  // Truncate on a word boundary where there is one, so a clipped slug still reads
  // as words rather than ending mid-syllable.
  const clipped = slug.slice(0, MAX_LENGTH)
  const lastHyphen = clipped.lastIndexOf('-')
  return (lastHyphen > 0 ? clipped.slice(0, lastHyphen) : clipped).replace(/-$/, '')
}

/**
 * Whether a slug the user typed (or one we derived) is usable. Empty is the case
 * that actually happens: a title of only punctuation or non-Latin characters
 * slugifies to nothing, and the create button must be disabled rather than sending a
 * request the server will refuse.
 */
export function isUsableSlug(slug: string): boolean {
  return slug.trim().length > 0
}
