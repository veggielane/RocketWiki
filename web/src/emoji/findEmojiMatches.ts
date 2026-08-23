/**
 * Candidate matching for `:name:` in plain text (design.md §19): find
 * grammar-shaped candidates between colons, then keep only names the
 * registry actually knows — an unknown `:name:` (or a grammar-shaped
 * accident like the "30" in `10:30:45`) is simply not an emoji and stays
 * literal text. No error state exists here by design: content synced from
 * an instance with a richer registry degrades to readable text, never to
 * broken UI.
 */

/** One char-set with the backend grammar (EmojiName.Pattern), inlined for the scanner's candidate shape. */
const CANDIDATE = /:([a-z0-9_-]{1,64}):/g

export interface EmojiMatch {
  name: string
  /** Start offset of the opening colon (inclusive). */
  from: number
  /** End offset just past the closing colon (exclusive). */
  to: number
}

/**
 * Scans `text` for renderable emoji occurrences. A matched candidate whose
 * name isn't known does NOT consume its closing colon — it may be the
 * opening colon of a real emoji (`:notreal:smile:` renders `:smile:`), and
 * adjacent emojis (`:a::b:`) each match in turn.
 */
export function findEmojiMatches(text: string, isKnown: (name: string) => boolean): EmojiMatch[] {
  const matches: EmojiMatch[] = []
  CANDIDATE.lastIndex = 0
  let match = CANDIDATE.exec(text)
  while (match !== null) {
    const name = match[1]
    if (isKnown(name)) {
      matches.push({ name, from: match.index, to: match.index + match[0].length })
      // lastIndex already sits just past the closing colon.
    } else {
      // Unknown: re-consider the closing colon as a potential opening one.
      CANDIDATE.lastIndex = match.index + match[0].length - 1
    }
    match = CANDIDATE.exec(text)
  }
  return matches
}
