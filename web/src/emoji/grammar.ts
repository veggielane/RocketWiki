/**
 * The emoji-name grammar, ported verbatim from the backend's
 * `EmojiName.Pattern` (src/RocketWiki.Core/Content/EmojiName.cs — design.md
 * §19): lowercase letters, digits, `_` and `-`, 1–64 characters. Lowercase-
 * only makes uniqueness case-insensitive by construction. The server is the
 * enforcement point; this copy exists so the admin form can refuse a bad
 * name with designed copy before a request, and so the renderer's candidate
 * matching uses exactly the character set a registered name can contain.
 */
export const EMOJI_NAME_PATTERN = '^[a-z0-9_-]{1,64}$'

export const EMOJI_NAME_MAX_LENGTH = 64

const VALID_NAME = new RegExp(EMOJI_NAME_PATTERN)

export function isValidEmojiName(name: string | null | undefined): boolean {
  return typeof name === 'string' && name.length <= EMOJI_NAME_MAX_LENGTH && VALID_NAME.test(name)
}
