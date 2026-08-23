import { EMOJI_NAME_MAX_LENGTH } from '../../emoji/grammar'

/**
 * The pure "is the user typing an emoji?" matcher behind the `:`
 * autocomplete: given the text of the current block up to the caret,
 * detects a trailing `:partial-name`.
 *
 * Rules, chosen to make prose colons quiet:
 * - the `:` must sit at the block start or after a non-name character, so
 *   `10:3` (colon after a digit) never triggers — mirroring the renderer,
 *   where "30" in `10:30:45` is a candidate the registry filter rejects;
 *   here the *author* isn't asking for an emoji at all. A `:` after a
 *   completed emoji's closing colon *does* trigger (adjacent emojis).
 * - at least one query character before the popup appears — a bare `:` is
 *   overwhelmingly punctuation; full-registry browsing is the toolbar
 *   picker's job.
 */
export interface EmojiInputMatch {
  /** The partial name after the colon (1..64 chars, grammar alphabet). */
  query: string
  /** Total matched length including the opening colon — chars back from the caret to the `:`. */
  length: number
}

const TRAILING_EMOJI_INPUT = new RegExp(`(?:^|[^a-z0-9_-])(:([a-z0-9_-]{1,${EMOJI_NAME_MAX_LENGTH}}))$`)

export function matchEmojiInput(textBeforeCaret: string): EmojiInputMatch | null {
  const match = TRAILING_EMOJI_INPUT.exec(textBeforeCaret)
  if (!match) return null
  return { query: match[2], length: match[1].length }
}

/** Prefix filter over the registry names, alphabetical, capped for the popup. */
export function filterEmojiNames(names: Iterable<string>, query: string, limit = 8): string[] {
  const result: string[] = []
  for (const name of [...names].sort()) {
    if (name.startsWith(query)) {
      result.push(name)
      if (result.length >= limit) break
    }
  }
  return result
}
