import { describe, expect, it } from 'vitest'
import { computeStaleDiff, isUnchanged, type DiffLine } from '../staleDiff'

const kinds = (lines: DiffLine[]) => lines.map((l) => `${l.kind}:${l.text}`)

describe('computeStaleDiff', () => {
  it('identical documents produce only unchanged lines', () => {
    const lines = computeStaleDiff('# Title\n\nBody text.\n', '# Title\n\nBody text.\n')
    expect(kinds(lines)).toEqual(['unchanged:# Title', 'unchanged:', 'unchanged:Body text.'])
    expect(isUnchanged(lines)).toBe(true)
  })

  it('two empty documents diff to nothing', () => {
    const lines = computeStaleDiff('', '')
    expect(lines).toEqual([])
    expect(isUnchanged(lines)).toBe(true)
  })

  it('a line only in their content is added (orientation: draft → theirs)', () => {
    const lines = computeStaleDiff('one\ntwo\n', 'one\nextra\ntwo\n')
    expect(kinds(lines)).toEqual(['unchanged:one', 'added:extra', 'unchanged:two'])
    expect(isUnchanged(lines)).toBe(false)
  })

  it('a line only in the draft is removed', () => {
    const lines = computeStaleDiff('one\nmine only\ntwo\n', 'one\ntwo\n')
    expect(kinds(lines)).toEqual(['unchanged:one', 'removed:mine only', 'unchanged:two'])
  })

  it('an empty draft against real content is all added', () => {
    expect(kinds(computeStaleDiff('', 'a\nb\n'))).toEqual(['added:a', 'added:b'])
  })

  it('handles documents without a trailing newline', () => {
    expect(kinds(computeStaleDiff('a\nb', 'a\nc'))).toEqual(['unchanged:a', 'removed:b', 'added:c'])
  })

  it('a changed line yields a removed/added pair with word-level segments', () => {
    const lines = computeStaleDiff('The quick brown fox.\n', 'The slow brown fox.\n')
    expect(lines.map((l) => l.kind)).toEqual(['removed', 'added'])

    const [removed, added] = lines
    // Segments reassemble exactly into the line — nothing invented or lost.
    expect(removed.segments!.map((s) => s.text).join('')).toBe('The quick brown fox.')
    expect(added.segments!.map((s) => s.text).join('')).toBe('The slow brown fox.')
    // Only the differing word is marked, on each side.
    expect(removed.segments!.filter((s) => s.changed).map((s) => s.text)).toEqual(['quick'])
    expect(added.segments!.filter((s) => s.changed).map((s) => s.text)).toEqual(['slow'])
  })

  it('a full rewrite gets no word-level segments (similarity guard)', () => {
    const lines = computeStaleDiff('alpha beta gamma\n', 'entirely different words here\n')
    expect(lines.map((l) => l.kind)).toEqual(['removed', 'added'])
    expect(lines[0].segments).toBeNull()
    expect(lines[1].segments).toBeNull()
  })

  it('pairs a replaced block line-by-line; unpaired surplus lines get no segments', () => {
    const lines = computeStaleDiff('shared start one\nshared start two\n', 'shared start ONE\nshared start TWO\nbrand new\n')
    expect(lines.map((l) => l.kind)).toEqual(['removed', 'removed', 'added', 'added', 'added'])
    // First removed pairs with first added, second with second.
    expect(lines[0].segments!.filter((s) => s.changed).map((s) => s.text)).toEqual(['one'])
    expect(lines[2].segments!.filter((s) => s.changed).map((s) => s.text)).toEqual(['ONE'])
    expect(lines[1].segments!.filter((s) => s.changed).map((s) => s.text)).toEqual(['two'])
    expect(lines[3].segments!.filter((s) => s.changed).map((s) => s.text)).toEqual(['TWO'])
    // The third added line had no counterpart to pair with.
    expect(lines[4].segments).toBeNull()
  })

  it('unchanged runs inside a paired line are not marked', () => {
    const [removed, added] = computeStaleDiff('keep this word\n', 'keep that word\n')
    expect(removed.segments!.filter((s) => !s.changed).map((s) => s.text).join('')).toBe('keep  word')
    expect(added.segments!.filter((s) => !s.changed).map((s) => s.text).join('')).toBe('keep  word')
  })
})
