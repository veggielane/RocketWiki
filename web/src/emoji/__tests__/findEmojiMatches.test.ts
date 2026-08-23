import { describe, expect, it } from 'vitest'
import { findEmojiMatches } from '../findEmojiMatches'

const known = (...names: string[]) => {
  const set = new Set(names)
  return (name: string) => set.has(name)
}

describe('findEmojiMatches — candidates between colons, filtered by the registry', () => {
  it('matches a known name mid-sentence', () => {
    expect(findEmojiMatches('launch day :rocket: at last', known('rocket'))).toEqual([
      { name: 'rocket', from: 11, to: 19 },
    ])
  })

  it('leaves an unknown name as literal text — no match, no error state', () => {
    expect(findEmojiMatches('this :notreal: stays text', known('rocket'))).toEqual([])
  })

  it('does not treat clock times as emojis: 10:30:45 has a grammar-shaped "30" candidate the registry rejects', () => {
    expect(findEmojiMatches('meeting at 10:30:45 sharp', known('rocket'))).toEqual([])
  })

  it('a registry that DID define "30" would match inside 10:30:45 — the registry filter is the only guard, by design', () => {
    expect(findEmojiMatches('10:30:45', known('30'))).toEqual([{ name: '30', from: 2, to: 6 }])
  })

  it('matches adjacent emojis back to back', () => {
    expect(findEmojiMatches(':a::b:', known('a', 'b'))).toEqual([
      { name: 'a', from: 0, to: 3 },
      { name: 'b', from: 3, to: 6 },
    ])
  })

  it("an unknown candidate's closing colon can open the next, real one", () => {
    expect(findEmojiMatches(':notreal:smile:', known('smile'))).toEqual([{ name: 'smile', from: 8, to: 15 }])
  })

  it('colons around non-grammar text never match', () => {
    expect(findEmojiMatches('see :Chapter One: and :sm ile:', known('chapter', 'sm', 'ile'))).toEqual([])
  })

  it('ignores an unclosed trailing colon', () => {
    expect(findEmojiMatches('almost :rocket', known('rocket'))).toEqual([])
  })

  it('returns every occurrence of a repeated emoji', () => {
    expect(findEmojiMatches(':x: and :x:', known('x'))).toEqual([
      { name: 'x', from: 0, to: 3 },
      { name: 'x', from: 8, to: 11 },
    ])
  })

  it('empty text and empty registry are quiet no-ops', () => {
    expect(findEmojiMatches('', known('x'))).toEqual([])
    expect(findEmojiMatches(':x:', () => false)).toEqual([])
  })
})
