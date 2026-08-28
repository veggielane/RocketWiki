import { describe, expect, it } from 'vitest'
import { buildRqlFromBuilder, quoteRqlValue, type PageListBuilderState } from '../rqlBuilder'

const base: PageListBuilderState = { labels: [], labelMode: 'all', spaceKeys: [], sort: 'default' }

describe('quoteRqlValue', () => {
  it('always quotes, so a value that spells a keyword cannot re-lex as syntax', () => {
    expect(quoteRqlValue('in')).toBe('"in"')
    expect(quoteRqlValue('NOT')).toBe('"NOT"')
  })

  it('escapes the closed escape set (§22.1)', () => {
    expect(quoteRqlValue('say "hi"')).toBe('"say \\"hi\\""')
    expect(quoteRqlValue('back\\slash')).toBe('"back\\\\slash"')
    expect(quoteRqlValue('a\nb\tc')).toBe('"a\\nb\\tc"')
  })
})

describe('builder → RQL', () => {
  it('a single label needs no combining operator', () => {
    expect(buildRqlFromBuilder({ ...base, labels: ['safety'] })).toBe('label = "safety"')
  })

  it('AND mode chains equality predicates — every label must be present', () => {
    expect(buildRqlFromBuilder({ ...base, labels: ['safety', 'review'], labelMode: 'all' })).toBe(
      'label = "safety" AND label = "review"',
    )
  })

  it('OR mode prints an IN list — same predicate, and no parentheses to reason about', () => {
    expect(buildRqlFromBuilder({ ...base, labels: ['draft', 'review'], labelMode: 'any' })).toBe(
      'label IN ("draft", "review")',
    )
  })

  it('the AND/OR toggle is the only difference between two otherwise identical states', () => {
    const labels = ['draft', 'review']
    expect(buildRqlFromBuilder({ ...base, labels, labelMode: 'all' })).not.toBe(
      buildRqlFromBuilder({ ...base, labels, labelMode: 'any' }),
    )
  })

  it('the toggle does not change a one-label filter, because there is nothing to combine', () => {
    expect(buildRqlFromBuilder({ ...base, labels: ['safety'], labelMode: 'all' })).toBe(
      buildRqlFromBuilder({ ...base, labels: ['safety'], labelMode: 'any' }),
    )
  })

  it('spaces narrow the filter with AND, one key or many', () => {
    expect(buildRqlFromBuilder({ ...base, labels: ['safety'], spaceKeys: ['ENG'] })).toBe(
      'label = "safety" AND space = "ENG"',
    )
    expect(buildRqlFromBuilder({ ...base, labels: ['safety'], spaceKeys: ['ENG', 'OPS'] })).toBe(
      'label = "safety" AND space IN ("ENG", "OPS")',
    )
  })

  it('spaces alone are a query on their own', () => {
    expect(buildRqlFromBuilder({ ...base, spaceKeys: ['ENG'] })).toBe('space = "ENG"')
  })

  it('an OR label list stays composable with a space clause without parentheses', () => {
    expect(buildRqlFromBuilder({ ...base, labels: ['a', 'b'], labelMode: 'any', spaceKeys: ['ENG', 'OPS'] })).toBe(
      'label IN ("a", "b") AND space IN ("ENG", "OPS")',
    )
  })

  it('the default sort prints NO clause — §22.2 applies updated DESC at execution', () => {
    expect(buildRqlFromBuilder({ ...base, labels: ['safety'], sort: 'default' })).toBe('label = "safety"')
  })

  it('every other sort prints an ORDER BY over the three allowed fields', () => {
    expect(buildRqlFromBuilder({ ...base, labels: ['a'], sort: 'updated-asc' })).toBe('label = "a" ORDER BY updated ASC')
    expect(buildRqlFromBuilder({ ...base, labels: ['a'], sort: 'created-desc' })).toBe(
      'label = "a" ORDER BY created DESC',
    )
    expect(buildRqlFromBuilder({ ...base, labels: ['a'], sort: 'title-asc' })).toBe('label = "a" ORDER BY title ASC')
  })

  it('nothing selected prints nothing — ORDER BY alone is not a query in §22.1', () => {
    expect(buildRqlFromBuilder({ ...base, sort: 'title-asc' })).toBe('')
  })

  it('a label spelling a keyword is safe by construction', () => {
    expect(buildRqlFromBuilder({ ...base, labels: ['in'] })).toBe('label = "in"')
  })
})
