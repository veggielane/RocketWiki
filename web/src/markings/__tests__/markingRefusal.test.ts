import { describe, expect, it } from 'vitest'
import {
  canonicalCountries,
  canonicalCountry,
  describeMarkingRefusal,
  eyesOnlyAdmits,
  markingRefusal,
  selectorGranted,
  type DraftMarking,
  type MarkingViewer,
} from '../markingRefusal'

/**
 * The two read gates a marking can shut its own author out of, client-side.
 * These tests are the reason the marking control is allowed to grey anything
 * out: the UI is only entitled to prevent what the server would actually
 * refuse, so this suite pins the mirror to `MarkingGate`'s rules rather than
 * to what looked reasonable.
 *
 * No level appears anywhere below, and that is the shape of the suite rather
 * than an omission: this deployment compares no classification against a
 * person, so a draft has no level to be refused for and the module has no
 * input for one. The type-level pin at the bottom keeps it that way.
 */
describe('country canonical form (design.md §21.4)', () => {
  it('trims and upper-cases', () => {
    expect(canonicalCountry('  uk ')).toBe('UK')
  })

  it('de-duplicates and sorts ordinally, and drops blanks', () => {
    expect(canonicalCountries(['us', 'UK', ' us ', '', '   '])).toEqual(['UK', 'US'])
  })
})

describe('eyesOnlyAdmits (design.md §21.4)', () => {
  it('admits everyone when the set is empty — an empty set means no caveat', () => {
    expect(eyesOnlyAdmits([], [])).toBe(true)
  })

  it('admits on a non-empty intersection', () => {
    expect(eyesOnlyAdmits(['UK', 'US'], ['US'])).toBe(true)
  })

  it('denies when the principal holds none of the set', () => {
    expect(eyesOnlyAdmits(['US'], ['UK'])).toBe(false)
  })

  it('denies an absent nationality against any caveat — fail closed', () => {
    expect(eyesOnlyAdmits(['UK'], [])).toBe(false)
  })

  it('folds case on both sides, which §21.4 documents as its one departure from ordinal matching', () => {
    expect(eyesOnlyAdmits([' uk '], ['Uk'])).toBe(true)
  })
})

describe('the selector grant gate (design.md §21.15)', () => {
  it('a grant is by category AND value — APPLE granted says nothing about BANANA', () => {
    const grants = [{ category: 'FRUIT', value: 'APPLE' }]
    expect(selectorGranted({ category: 'FRUIT', value: 'APPLE' }, grants)).toBe(true)
    expect(selectorGranted({ category: 'FRUIT', value: 'BANANA' }, grants)).toBe(false)
    expect(selectorGranted({ category: 'REGION', value: 'APPLE' }, grants)).toBe(false)
    expect(selectorGranted({ category: 'fruit', value: 'apple' }, grants)).toBe(true)
  })

  it('no grants at all means nothing is granted — there is no category anyone is entitled to by default', () => {
    expect(selectorGranted({ category: 'FRUIT', value: 'APPLE' }, [])).toBe(false)
  })
})

const ukEditor: MarkingViewer = {
  nationality: ['UK'],
  selectorGrants: [{ category: 'FRUIT', value: 'APPLE' }],
}

describe('markingRefusal — the self-lockout rule mirrored so the UI prevents rather than refuses', () => {
  it('finds nothing wrong with a marking the caller could still read', () => {
    expect(
      markingRefusal({ eyesOnly: ['UK', 'US'], selectors: [{ category: 'FRUIT', value: 'APPLE' }] }, ukEditor),
    ).toBeNull()
  })

  it('refuses a selector value no access grant confers on the caller in this space', () => {
    expect(markingRefusal({ eyesOnly: [], selectors: [{ category: 'FRUIT', value: 'BANANA' }] }, ukEditor)).toEqual({
      kind: 'SELECTOR_NOT_GRANTED',
      category: 'FRUIT',
      value: 'BANANA',
    })
  })

  it('refuses a value in a category the caller holds no grant in at all — there is no eligibility to fall back on', () => {
    // What used to be an eligibility refusal is a grant refusal now: REGION
    // is gated by grants like every other category, and this caller has none.
    expect(markingRefusal({ eyesOnly: [], selectors: [{ category: 'REGION', value: 'NORTH' }] }, ukEditor)).toEqual({
      kind: 'SELECTOR_NOT_GRANTED',
      category: 'REGION',
      value: 'NORTH',
    })
  })

  it('claims no grant refusal while the grants are unknown — the server still decides', () => {
    // The grants read may not have answered; a refusal the SPA cannot justify
    // would hide a value from someone entitled to it.
    expect(
      markingRefusal(
        { eyesOnly: [], selectors: [{ category: 'FRUIT', value: 'BANANA' }] },
        { ...ukEditor, selectorGrants: null },
      ),
    ).toBeNull()
  })

  it('refuses an eyes-only set that excludes the caller', () => {
    // SECRET US EYES ONLY set by a UK national loses them the page just as
    // completely as any other marking they could not read.
    expect(markingRefusal({ eyesOnly: ['US'], selectors: [] }, ukEditor)).toEqual({
      kind: 'EYES_ONLY_EXCLUDES_YOU',
      viewerHasNoNationality: false,
    })
  })

  it("reports the gates in ladder order — each selector's grant, in the order listed, then the caveat", () => {
    const failsEverything: DraftMarking = {
      eyesOnly: ['US'],
      selectors: [
        { category: 'FRUIT', value: 'BANANA' },
        { category: 'REGION', value: 'NORTH' },
      ],
    }
    expect(markingRefusal(failsEverything, ukEditor)).toEqual({
      kind: 'SELECTOR_NOT_GRANTED',
      category: 'FRUIT',
      value: 'BANANA',
    })
    expect(markingRefusal({ ...failsEverything, selectors: [failsEverything.selectors[1]!] }, ukEditor)).toEqual({
      kind: 'SELECTOR_NOT_GRANTED',
      category: 'REGION',
      value: 'NORTH',
    })
    // Every selector fine: the caveat is last.
    expect(markingRefusal({ ...failsEverything, selectors: [] }, ukEditor)?.kind).toBe('EYES_ONLY_EXCLUDES_YOU')
  })

  it('distinguishes "you hold no nationality at all" so the copy can say the more useful thing', () => {
    expect(markingRefusal({ eyesOnly: ['UK'], selectors: [] }, { ...ukEditor, nationality: [] })).toEqual({
      kind: 'EYES_ONLY_EXCLUDES_YOU',
      viewerHasNoNationality: true,
    })
  })

  it('survives a nationality that arrives in any case or padding — the comparison canonicalizes both sides', () => {
    // A false refusal in the affordance whose entire purpose is to predict
    // the server's answer would warn an author out of a marking that would
    // have worked. Also guards the tempting simplification "the server
    // already canonicalizes `me.nationality`, so we can drop ours".
    for (const held of [['nz'], [' NZ '], ['Nz']]) {
      expect(markingRefusal({ eyesOnly: ['NZ'], selectors: [] }, { ...ukEditor, nationality: held })).toBeNull()
    }
  })

  it('has no input for the level or the UK prefix — neither is compared against anyone', () => {
    // Structural, and pinned at the type level below: a draft is selectors
    // and a caveat, so nothing about a level or a prefix can change a
    // verdict. The runtime half keeps the test from passing vacuously.
    const draft = { eyesOnly: ['UK'], selectors: [] } satisfies DraftMarking
    expect(Object.keys(draft).sort()).toEqual(['eyesOnly', 'selectors'])
  })

  it('claims no refusal when the caller is unknown — the server still decides', () => {
    expect(markingRefusal({ eyesOnly: ['XX'], selectors: [{ category: 'FRUIT', value: 'BANANA' }] }, null)).toBeNull()
  })
})

describe('describeMarkingRefusal', () => {
  it('names the value for a selector refusal', () => {
    expect(describeMarkingRefusal({ kind: 'SELECTOR_NOT_GRANTED', category: 'FRUIT', value: 'BANANA' })).toBe(
      'BANANA is not granted to you in this space, so you could not read this page after marking it.',
    )
  })

  it('says which fact applies for the two eyes-only cases', () => {
    expect(describeMarkingRefusal({ kind: 'EYES_ONLY_EXCLUDES_YOU', viewerHasNoNationality: false })).toContain(
      'none of your own nationalities',
    )
    expect(describeMarkingRefusal({ kind: 'EYES_ONLY_EXCLUDES_YOU', viewerHasNoNationality: true })).toContain(
      'no nationality value',
    )
  })
})

type AssertNever<T extends never> = T
/** Compile-time only: fails to typecheck if `DraftMarking` ever grows a level or a prefix field. */
export type DraftHasNoLevelOrPrefix = AssertNever<Extract<keyof DraftMarking, 'level' | 'ukPrefix'>>
