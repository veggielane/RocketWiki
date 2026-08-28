import { describe, expect, it } from 'vitest'
import { offendingText, querySpans } from '../rqlErrorSpans'

const QUERY = 'label = "a" AND marking = SECRET'

describe('offendingText', () => {
  it('returns exactly the span an error points at', () => {
    // `marking` at offset 16, length 7 — §22.3's refused-by-name field.
    expect(offendingText(QUERY, { offset: 16, length: 7 })).toBe('marking')
  })

  // Degrading to LESS highlight is the safe direction: a nonsense offset must
  // never slide the window onto text from elsewhere in the query, which would
  // point the author at the wrong thing with full confidence.
  it('clamps rather than trusting offsets it did not measure', () => {
    expect(offendingText(QUERY, { offset: 900, length: 5 })).toBe('')
    expect(offendingText(QUERY, { offset: -4, length: 5 })).toBe('l')
    expect(offendingText(QUERY, { offset: 28, length: 900 })).toBe('CRET')
  })

  it('an empty span yields empty text, never a stray character', () => {
    expect(offendingText(QUERY, { offset: 5, length: 0 })).toBe('')
  })
})

describe('querySpans', () => {
  it('splits into unmarked and marked stretches', () => {
    expect(querySpans(QUERY, [{ offset: 16, length: 7 }])).toEqual([
      { text: 'label = "a" AND ', isError: false },
      { text: 'marking', isError: true },
      { text: ' = SECRET', isError: false },
    ])
  })

  it('joining the spans reproduces the query exactly, so no character is lost', () => {
    const spans = querySpans(QUERY, [
      { offset: 26, length: 6 },
      { offset: 16, length: 7 },
    ])
    expect(spans.map((s) => s.text).join('')).toBe(QUERY)
  })

  it('merges overlapping ranges instead of trusting them to be disjoint', () => {
    expect(
      querySpans('abcdef', [
        { offset: 1, length: 3 },
        { offset: 2, length: 3 },
      ]),
    ).toEqual([
      { text: 'a', isError: false },
      { text: 'bcde', isError: true },
      { text: 'f', isError: false },
    ])
  })

  it('no errors leaves one unmarked span', () => {
    expect(querySpans(QUERY, [])).toEqual([{ text: QUERY, isError: false }])
  })

  it('an empty query has no spans at all', () => {
    expect(querySpans('', [{ offset: 0, length: 3 }])).toEqual([])
  })

  it('an error covering the whole query leaves nothing unmarked', () => {
    expect(querySpans('abc', [{ offset: 0, length: 3 }])).toEqual([{ text: 'abc', isError: true }])
  })
})
