import { describe, expect, it } from 'vitest'
import { EMOJI_NAME_MAX_LENGTH, EMOJI_NAME_PATTERN, isValidEmojiName } from '../grammar'

/**
 * Parity with the backend's EmojiName (src/RocketWiki.Core/Content/EmojiName.cs):
 * the pattern constant is ported verbatim, and the vectors below assert the
 * behavior both sides must agree on. If the server grammar ever changes,
 * this file is the tripwire on the SPA side.
 */
describe('emoji name grammar — ported constant', () => {
  it('is the exact backend pattern', () => {
    expect(EMOJI_NAME_PATTERN).toBe('^[a-z0-9_-]{1,64}$')
    expect(EMOJI_NAME_MAX_LENGTH).toBe(64)
  })

  it.each([
    'smile',
    'a',
    '0',
    'rocket-2',
    'snake_case',
    '-',
    '_',
    'a'.repeat(64),
  ])('accepts %j', (name) => {
    expect(isValidEmojiName(name)).toBe(true)
  })

  it.each([
    '',
    'a'.repeat(65),
    'Smile', // uppercase — lowercase-only makes uniqueness case-insensitive by construction
    'sm ile',
    'émoji',
    'name!',
    ':smile:',
    'smile:',
    'tab\tname',
  ])('rejects %j', (name) => {
    expect(isValidEmojiName(name)).toBe(false)
  })

  it('rejects null/undefined without throwing', () => {
    expect(isValidEmojiName(null)).toBe(false)
    expect(isValidEmojiName(undefined)).toBe(false)
  })
})
