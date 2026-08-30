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
 * The one slug a page may not take. Every system page for a space lives under a `-`
 * segment (/spaces/{key}/-/admin), so that segment is the only thing a page address
 * can collide with — one reserved token instead of a list that had to grow with the
 * router and would silently shadow pages when someone forgot.
 *
 * The SERVER is the enforcement point (Core's PageSlugs); this exists so the dialog
 * can say so before the round trip. Only the exact string: "admin-guide" is fine, and
 * slugifyTitle can never produce a bare "-" because it trims leading and trailing
 * hyphens.
 */
export const SYSTEM_SEGMENT = '-'

export function isReservedSlug(slug: string): boolean {
  return slug.trim() === SYSTEM_SEGMENT
}

/**
 * Whether a slug the user typed (or one we derived) is usable. Empty is the case
 * that actually happens: a title of only punctuation or non-Latin characters
 * slugifies to nothing, and the create button must be disabled rather than sending a
 * request the server will refuse.
 */
export function isUsableSlug(slug: string): boolean {
  return slug.trim().length > 0 && !isReservedSlug(slug)
}

/**
 * The stored form of a slug someone typed by hand.
 *
 * URLs are case-insensitive: the server stores slugs lowercased and resolves a
 * request whatever case it arrives in. Case-folding ONLY — deliberately not the
 * full `slugifyTitle` treatment, which strips punctuation, collapses whitespace
 * to hyphens and trims leading/trailing ones. Running that on every keystroke
 * would fight the typist: a typed space would become a hyphen before the next
 * letter arrived, and a trailing hyphen would be eaten as it was typed, so
 * "post-mortem" could not be reached one character at a time. Lowercasing is
 * idempotent, removes nothing and cannot move the caret, which is what makes it
 * safe to apply live.
 */
export function canonicalSlug(slug: string): string {
  return slug.toLowerCase()
}

/**
 * Do a stored slug and one out of a URL name the same page?
 *
 * The server now resolves `/spaces/ENG/My-Page` to the same page as
 * `/spaces/eng/my-page`, so a client-side `===` against a route param would
 * make a correct URL half-work: the page loads (the server resolved it) while
 * the tree fails to highlight it and the crumb disagrees, which reads as a bug
 * in the tree rather than as a URL casing difference.
 */
export function sameSlug(a: string | null | undefined, b: string | null | undefined): boolean {
  return a != null && b != null && a.toLowerCase() === b.toLowerCase()
}

/** The same rule for space keys, which are canonicalised server-side too. */
export function sameSpaceKey(a: string | null | undefined, b: string | null | undefined): boolean {
  return a != null && b != null && a.toLowerCase() === b.toLowerCase()
}

/**
 * Where to send someone who should end up looking at a page.
 *
 * Prefers the readable address, because that is what someone copies out of the
 * address bar and pastes to a colleague — landing them on /pages/{id} after
 * every save would mean they never see the good URL in ordinary use. Falls back
 * to the id route rather than guessing when the caller has no slug or space key
 * to hand: /pages/{id} always resolves, so the fallback is correctness, not a
 * degraded experience.
 */
export function pageHref(
  spaceKey: string | null | undefined,
  slug: string | null | undefined,
  pageId: string,
): string {
  return spaceKey && slug && isUsableSlug(slug)
    ? `/spaces/${encodeURIComponent(spaceKey)}/${encodeURIComponent(slug.trim())}`
    : `/pages/${pageId}`
}
