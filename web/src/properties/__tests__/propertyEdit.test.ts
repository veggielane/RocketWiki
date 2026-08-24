import { describe, expect, it } from 'vitest'
import { hasPendingEdits, resolvePropertyEdit } from '../propertyEdit'

/**
 * The gesture-to-mutation translation the properties screen depends on
 * (design.md §20). The backend keeps `setPageProperty` to one meaning and
 * refuses an empty value; every case where that refusal *would* fire has to
 * resolve to something else here, because none of them is a user error.
 */
describe('resolvePropertyEdit', () => {
  it('sends nothing when the value is untouched', () => {
    expect(resolvePropertyEdit('Ada Lovelace', 'Ada Lovelace')).toEqual({ kind: 'none' })
  })

  it('sets a changed value verbatim, without normalizing what was typed', () => {
    expect(resolvePropertyEdit('  Ada Lovelace ', 'Ada Lovelace')).toEqual({ kind: 'set', value: '  Ada Lovelace ' })
  })

  it('turns a cleared field into a remove — never a set with an empty value', () => {
    expect(resolvePropertyEdit('', 'Draft')).toEqual({ kind: 'remove' })
  })

  it('treats whitespace-only as cleared, because that is exactly what the server refuses', () => {
    expect(resolvePropertyEdit('   \t ', 'Draft')).toEqual({ kind: 'remove' })
  })

  it('sends nothing when a never-saved row is left empty — removing it would be refused too', () => {
    expect(resolvePropertyEdit('', null)).toEqual({ kind: 'none' })
    expect(resolvePropertyEdit('  ', null)).toEqual({ kind: 'none' })
  })

  it('sets a value on a row the page does not carry yet', () => {
    expect(resolvePropertyEdit('Draft', null)).toEqual({ kind: 'set', value: 'Draft' })
  })
})

describe('hasPendingEdits', () => {
  it('is false when every row resolves to no mutation', () => {
    expect(
      hasPendingEdits([
        { draft: 'Draft', storedValue: 'Draft' },
        { draft: '', storedValue: null },
      ]),
    ).toBe(false)
  })

  it('is true for a clear, so Save stays available for the gesture that removes a row', () => {
    expect(hasPendingEdits([{ draft: '', storedValue: 'Draft' }])).toBe(true)
  })
})
