import { describe, expect, it } from 'vitest'
import { describeDeleteOutcome } from '../describeDeleteOutcome'

describe('describeDeleteOutcome', () => {
  it('returns null for a fully successful delete (no blocked count)', () => {
    expect(describeDeleteOutcome(null)).toBeNull()
    expect(describeDeleteOutcome(undefined)).toBeNull()
    expect(describeDeleteOutcome(0)).toBeNull()
  })

  it('uses singular "page" for exactly one blocked page', () => {
    expect(describeDeleteOutcome(1)).toBe("1 page in this subtree couldn't be deleted (you don't have permission).")
  })

  it('uses plural "pages" for more than one', () => {
    expect(describeDeleteOutcome(3)).toBe("3 pages in this subtree couldn't be deleted (you don't have permission).")
  })

  it('never mentions specific page identities — the message is count-only by construction', () => {
    const message = describeDeleteOutcome(5)!
    expect(message).not.toMatch(/page-|title|id:/i)
  })
})
