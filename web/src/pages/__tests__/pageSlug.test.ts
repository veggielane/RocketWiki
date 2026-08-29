import { describe, expect, it } from 'vitest'
import { isReservedSlug, isUsableSlug, slugifyTitle } from '../pageSlug'

describe('slugifyTitle', () => {
  it('lowercases and hyphenates', () => {
    expect(slugifyTitle('Chill-in Checklist')).toBe('chill-in-checklist')
  })

  it('drops characters a URL should not carry', () => {
    expect(slugifyTitle('Igniter: notes (draft!) #2')).toBe('igniter-notes-draft-2')
  })

  it('collapses runs of separators rather than leaving empty segments', () => {
    expect(slugifyTitle('Pad   39A  ---  notes')).toBe('pad-39a-notes')
  })

  it('trims leading and trailing separators', () => {
    expect(slugifyTitle('  --Static fire--  ')).toBe('static-fire')
  })

  it('returns empty for a title with nothing sluggable, rather than inventing one', () => {
    // The heading-anchor slugifier substitutes "section" here because an anchor
    // must exist; a page slug must NOT be invented, because the user is about to
    // see it in a URL and should be asked instead.
    expect(slugifyTitle('!!!')).toBe('')
    expect(slugifyTitle('日本語')).toBe('')
  })

  it('caps length on a word boundary so a long title stays readable', () => {
    const slug = slugifyTitle('the quick brown fox jumps over the lazy dog and then keeps on running well past eighty characters')
    expect(slug.length).toBeLessThanOrEqual(80)
    expect(slug.endsWith('-')).toBe(false)
    // Cut between words, not mid-word.
    expect('the quick brown fox jumps over the lazy dog and then keeps on running well past eighty characters'
      .split(' ')).toContain(slug.split('-').at(-1))
  })
})

describe('isUsableSlug', () => {
  it('rejects empty and whitespace', () => {
    expect(isUsableSlug('')).toBe(false)
    expect(isUsableSlug('   ')).toBe(false)
  })

  it('accepts a real slug', () => {
    expect(isUsableSlug('launch-notes')).toBe(true)
  })
})

describe('the reserved system segment', () => {
  it('refuses the bare "-" a system route owns', () => {
    expect(isReservedSlug('-')).toBe(true)
    expect(isUsableSlug('-')).toBe(false)
    expect(isReservedSlug('  -  ')).toBe(true)
  })

  it('allows everything that merely looks systemish', () => {
    // Under the old per-word list these were all refused; with system pages behind
    // /spaces/{key}/-/ only the segment itself collides.
    for (const fine of ['admin', 'grants', 'trash', 'import-report', 'admin-guide', '--']) {
      expect(isReservedSlug(fine)).toBe(false)
      expect(isUsableSlug(fine)).toBe(true)
    }
  })

  it('is unreachable from a title, since slugify trims hyphens', () => {
    expect(slugifyTitle('---')).toBe('')
  })
})
