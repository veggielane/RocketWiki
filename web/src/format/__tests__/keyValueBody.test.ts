import { describe, expect, it } from 'vitest'
import { parseKeyValueBody } from '../keyValueBody'

describe('parseKeyValueBody', () => {
  it('splits at the FIRST equals sign — values may contain =', () => {
    const entries = parseKeyValueBody('search=a=b')
    expect(entries.get('search')).toBe('a=b')
  })

  // The page-list fence (design.md §22) depends on this completely: an RQL
  // string is mostly equals signs, and `query = label = "safety"` has to come
  // back as one key with the whole query as its value.
  it('a whole RQL query survives as one value', () => {
    const entries = parseKeyValueBody('query = label = "safety" AND space IN ("ENG")')
    expect(entries.get('query')).toBe('label = "safety" AND space IN ("ENG")')
  })

  it('ignores blank lines and lines without =, keeps the last duplicate', () => {
    const entries = parseKeyValueBody('project=one\n\nnot a pair\nproject=two')
    expect(entries.get('project')).toBe('two')
    expect(entries.size).toBe(1)
  })

  it('trims keys and values', () => {
    const entries = parseKeyValueBody('  ref = main  ')
    expect(entries.get('ref')).toBe('main')
  })
})
