import { describe, expect, it } from 'vitest'
import { entryMatches, parseEntryFilter, unknownFields } from '../entryFilter'
import type { FormFieldType } from '../../graphql/generated/graphql'

const types: Record<string, FormFieldType> = {
  severity: 'SELECT',
  summary: 'TEXT',
  occurredAt: 'DATE',
  costEstimate: 'NUMBER',
}
const typeOf = (field: string) => types[field] ?? 'TEXT'

function parsed(where: string) {
  const result = parseEntryFilter(where)
  if (!result.ok) throw new Error(`expected a parse, got: ${result.message}`)
  return result.conditions
}

describe('parseEntryFilter', () => {
  it('reads a comparison', () => {
    expect(parsed('severity = high')).toEqual([{ field: 'severity', operator: '=', values: ['high'] }])
  })

  it('reads every comparison operator, longest first', () => {
    // `>=` must not be read as `>` with a stray `=` — the ordering of the operator
    // list is the whole guard against that.
    expect(parsed('costEstimate >= 100')[0]!.operator).toBe('>=')
    expect(parsed('costEstimate <= 100')[0]!.operator).toBe('<=')
    expect(parsed('severity != low')[0]!.operator).toBe('!=')
    expect(parsed('costEstimate > 100')[0]!.operator).toBe('>')
  })

  it('reads IN and its list', () => {
    expect(parsed('severity IN (high, medium)')).toEqual([
      { field: 'severity', operator: 'IN', values: ['high', 'medium'] },
    ])
  })

  it('strips quotes so a value can contain spaces', () => {
    expect(parsed('summary = "turbopump stall"')[0]!.values).toEqual(['turbopump stall'])
  })

  it('joins conditions with AND', () => {
    expect(parsed('severity = high AND costEstimate > 100')).toHaveLength(2)
  })

  it('treats an empty filter as no filter', () => {
    expect(parseEntryFilter('')).toEqual({ ok: true, conditions: [] })
    expect(parseEntryFilter(undefined)).toEqual({ ok: true, conditions: [] })
  })

  it('refuses OR rather than half-implementing it', () => {
    // Mixing AND and OR needs precedence and parentheses, and a filter that
    // guessed at precedence would silently mean something other than it reads like.
    const result = parseEntryFilter('severity = high OR severity = medium')
    expect(result.ok).toBe(false)
    expect(!result.ok && result.message).toContain('OR is not supported')
  })

  it('refuses parentheses', () => {
    const result = parseEntryFilter('(severity = high)')
    expect(result.ok).toBe(false)
  })

  it('refuses a fragment it cannot read, naming it', () => {
    // §22.3: an ignored predicate is worse than a refused one, because the author
    // believes the filter applied and reads the rows as if it had.
    const result = parseEntryFilter('severity high')
    expect(result.ok).toBe(false)
    expect(!result.ok && result.message).toContain('severity high')
  })

  it('refuses an empty IN list', () => {
    expect(parseEntryFilter('severity IN ()').ok).toBe(false)
  })
})

describe('unknownFields', () => {
  it('names a field the form does not declare', () => {
    // A typo would otherwise match nothing and present as "no records yet" — the
    // author reads an empty table as a fact about the data rather than their filter.
    expect(unknownFields(parsed('sevrity = high'), Object.keys(types))).toEqual(['sevrity'])
  })

  it('accepts a declared field whatever its case', () => {
    expect(unknownFields(parsed('SEVERITY = high'), Object.keys(types))).toEqual([])
  })
})

describe('entryMatches', () => {
  const record = { severity: 'high', summary: 'Turbopump stall', occurredAt: '2026-08-14', costEstimate: '250' }

  it('matches equality case-insensitively', () => {
    expect(entryMatches(record, parsed('severity = HIGH'), typeOf)).toBe(true)
    expect(entryMatches(record, parsed('severity = low'), typeOf)).toBe(false)
  })

  it('matches IN against any listed value', () => {
    expect(entryMatches(record, parsed('severity IN (medium, high)'), typeOf)).toBe(true)
    expect(entryMatches(record, parsed('severity IN (low, medium)'), typeOf)).toBe(false)
  })

  it('compares a number field numerically, not as text', () => {
    // The trap: as strings, "250" < "9". A number field has to compare as numbers
    // or every threshold filter is quietly wrong.
    expect(entryMatches(record, parsed('costEstimate > 9'), typeOf)).toBe(true)
    expect(entryMatches(record, parsed('costEstimate < 9'), typeOf)).toBe(false)
  })

  it('compares dates as ISO strings, which orders correctly', () => {
    expect(entryMatches(record, parsed('occurredAt >= 2026-08-01'), typeOf)).toBe(true)
    expect(entryMatches(record, parsed('occurredAt > 2026-09-01'), typeOf)).toBe(false)
  })

  it('requires every condition', () => {
    expect(entryMatches(record, parsed('severity = high AND costEstimate > 100'), typeOf)).toBe(true)
    expect(entryMatches(record, parsed('severity = high AND costEstimate > 1000'), typeOf)).toBe(false)
  })

  it('treats a missing field as empty rather than throwing', () => {
    expect(entryMatches({}, parsed('severity = high'), typeOf)).toBe(false)
    expect(entryMatches({}, parsed('severity != high'), typeOf)).toBe(true)
  })

  it('does not match a non-numeric value in a number field, rather than throwing', () => {
    // Author-supplied data can predate the field's type; a comparison that cannot
    // be made is simply not a match.
    expect(entryMatches({ costEstimate: 'about 250' }, parsed('costEstimate > 9'), typeOf)).toBe(false)
  })
})
