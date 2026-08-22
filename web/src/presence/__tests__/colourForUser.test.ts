import { describe, expect, it } from 'vitest'
import { colourForUser } from '../colourForUser'

describe('colourForUser', () => {
  it('is deterministic for the same id', () => {
    expect(colourForUser('user-1')).toBe(colourForUser('user-1'))
  })

  it('differs for different ids (not a constant)', () => {
    expect(colourForUser('user-1')).not.toBe(colourForUser('user-2'))
  })

  it('produces a valid hsl() string', () => {
    expect(colourForUser('anyone')).toMatch(/^hsl\(\d+, 70%, 45%\)$/)
  })

  it('handles an empty string without throwing', () => {
    expect(() => colourForUser('')).not.toThrow()
  })
})
