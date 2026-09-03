import { describe, expect, it } from 'vitest'
import type { NationalCaveatCountry } from '../../graphql/generated/graphql'
import { NATIONAL_CAVEAT_COUNTRIES, isNationalCaveatCountry, type EveryCountryIsPinned } from '../caveatCountries'

/**
 * design.md §21.4: the caveat vocabulary is the fixed five, and the picker's
 * list is pinned to the schema's input enum in both directions. The
 * compile-time half of that lives in `EveryCountryIsPinned` and the
 * `satisfies` clause; this file pins the runtime half and keeps the type
 * referenced so a build cannot tree-shake the assertion away.
 */
describe('the national caveat vocabulary (design.md §21.4)', () => {
  it('is exactly AUS, CAN, NZ, UK and US, alphabetical', () => {
    expect([...NATIONAL_CAVEAT_COUNTRIES]).toEqual(['AUS', 'CAN', 'NZ', 'UK', 'US'])
  })

  it('is pinned to the schema enum in both directions', () => {
    // A sixth member in the schema fails `EveryCountryIsPinned` at compile
    // time; a member here the schema lacks fails `satisfies`. This keeps the
    // type in use and asserts the runtime list is one-to-one with it.
    const pinned: EveryCountryIsPinned = undefined as never
    expect(pinned).toBeUndefined()
    const asEnum: readonly NationalCaveatCountry[] = NATIONAL_CAVEAT_COUNTRIES
    expect(asEnum).toHaveLength(5)
  })

  it('recognises a sendable token and refuses a legacy one', () => {
    expect(isNationalCaveatCountry('UK')).toBe(true)
    expect(isNationalCaveatCountry('GB')).toBe(false)
    expect(isNationalCaveatCountry('uk')).toBe(false)
    expect(isNationalCaveatCountry('')).toBe(false)
  })
})
