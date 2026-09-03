import { describe, expect, it } from 'vitest'
import type { ClassificationLevel } from '../../graphql/generated/graphql'
import {
  CLASSIFICATION_LADDER,
  DEFAULT_CLEARANCE,
  canonicalCountries,
  canonicalCountry,
  clearanceRank,
  describeMarkingRefusal,
  eyesOnlyAdmits,
  levelIsWithinClearance,
  markingLevelRank,
  markingRefusal,
  selectorEligible,
  selectorGranted,
  type ViewerClearance,
} from '../clearance'

/**
 * design.md §21's gates, client-side. These tests are the reason the marking
 * control is allowed to grey anything out: the UI is only entitled to prevent
 * what the server would actually refuse, so this suite pins the mirror to
 * `MarkingGate`'s rules rather than to what looked reasonable.
 */
describe('the classification ladder (design.md §21.1)', () => {
  it('is the four levels, lowest to highest', () => {
    expect([...CLASSIFICATION_LADDER]).toEqual(['OFFICIAL', 'OFFICIAL_SENSITIVE', 'SECRET', 'TOP_SECRET'])
  })

  it('ranks strictly increasing, so a comparison can never tie two levels', () => {
    const ranks = CLASSIFICATION_LADDER.map(markingLevelRank)
    expect(ranks).toEqual([...ranks].sort((a, b) => a - b))
    expect(new Set(ranks).size).toBe(ranks.length)
  })
})

describe('fail-closed directions (design.md §21.3)', () => {
  // The two directions are deliberately opposite, and getting either backwards
  // is a silent bypass rather than a visible bug.
  it('ranks an unplaceable PAGE level above the top of the ladder', () => {
    expect(markingLevelRank('COSMIC' as ClassificationLevel)).toBeGreaterThan(markingLevelRank('TOP_SECRET'))
  })

  it('ranks an unplaceable CLEARANCE at the floor, OFFICIAL-SENSITIVE — and only there', () => {
    // The floor moved one notch up from OFFICIAL: the two everyday tiers stay
    // readable to a token with no usable clearance, and the line sits above
    // them. Not OFFICIAL (the old floor) and not the top.
    expect(DEFAULT_CLEARANCE).toBe('OFFICIAL_SENSITIVE')
    expect(clearanceRank('COSMIC' as ClassificationLevel)).toBe(clearanceRank('OFFICIAL_SENSITIVE'))
    expect(clearanceRank('COSMIC' as ClassificationLevel)).toBeGreaterThan(clearanceRank('OFFICIAL'))
    expect(clearanceRank('COSMIC' as ClassificationLevel)).toBeLessThan(clearanceRank('SECRET'))
  })

  it('never lets an unplaceable clearance reach an unplaceable level', () => {
    expect(levelIsWithinClearance('COSMIC' as ClassificationLevel, 'COSMIC' as ClassificationLevel)).toBe(false)
  })

  it('denies an unplaceable level even to TOP_SECRET — one notch stricter than the server, on purpose', () => {
    // Logged divergence, not a defect: `ProtectiveMarking` normalizes an
    // unknown level TO TOP SECRET, so the server would admit a TOP_SECRET
    // principal where this denies them. The only way the two can disagree is a
    // server ahead of this client, and an affordance should be the stricter
    // side of that gap — a greyed-out level the server might have allowed
    // costs a click, offering one it refuses is the whole failure mode.
    // Change this only alongside the comment on `markingLevelRank`.
    expect(levelIsWithinClearance('COSMIC' as ClassificationLevel, 'TOP_SECRET')).toBe(false)
  })
})

describe('levelIsWithinClearance (design.md §21.2)', () => {
  it('admits a level at or below the clearance', () => {
    expect(levelIsWithinClearance('OFFICIAL', 'SECRET')).toBe(true)
    expect(levelIsWithinClearance('SECRET', 'SECRET')).toBe(true)
  })

  it('refuses a level above the clearance', () => {
    expect(levelIsWithinClearance('TOP_SECRET', 'SECRET')).toBe(false)
    expect(levelIsWithinClearance('OFFICIAL_SENSITIVE', 'OFFICIAL')).toBe(false)
  })
})

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

describe('the selector gates (design.md §21.15)', () => {
  it('eligibility is by category name, case-folded on both sides', () => {
    expect(selectorEligible('FRUIT', ['FRUIT', 'REGION'])).toBe(true)
    expect(selectorEligible('fruit', [' Fruit '])).toBe(true)
    expect(selectorEligible('FRUIT', ['REGION'])).toBe(false)
    expect(selectorEligible('FRUIT', [])).toBe(false)
  })

  it('a grant is by category AND value — APPLE granted says nothing about BANANA', () => {
    const grants = [{ category: 'FRUIT', value: 'APPLE' }]
    expect(selectorGranted({ category: 'FRUIT', value: 'APPLE' }, grants)).toBe(true)
    expect(selectorGranted({ category: 'FRUIT', value: 'BANANA' }, grants)).toBe(false)
    expect(selectorGranted({ category: 'REGION', value: 'APPLE' }, grants)).toBe(false)
    expect(selectorGranted({ category: 'fruit', value: 'apple' }, grants)).toBe(true)
  })
})

const secretCleared: ViewerClearance = {
  clearance: 'SECRET',
  nationality: ['UK'],
  selectorEligibility: ['FRUIT'],
  selectorGrants: [{ category: 'FRUIT', value: 'APPLE' }],
}

describe('markingRefusal — §21.6 mirrored so the UI prevents rather than refuses', () => {
  it('finds nothing wrong with a marking the caller could still read', () => {
    expect(
      markingRefusal(
        { level: 'SECRET', eyesOnly: ['UK', 'US'], selectors: [{ category: 'FRUIT', value: 'APPLE' }] },
        secretCleared,
      ),
    ).toBeNull()
  })

  it('refuses a level above the caller clearance', () => {
    expect(markingRefusal({ level: 'TOP_SECRET', eyesOnly: [], selectors: [] }, secretCleared)).toEqual({
      kind: 'ABOVE_CLEARANCE',
      level: 'TOP_SECRET',
    })
  })

  it('refuses a selector in a category the caller is not eligible for, whatever the value', () => {
    // Bob's case from the truth table: `UK OFFICIAL BANANA` with a grant for
    // BANANA but no `fruit=yes` — granted but not eligible is still refused.
    expect(
      markingRefusal(
        { level: 'OFFICIAL', eyesOnly: [], selectors: [{ category: 'REGION', value: 'NORTH' }] },
        secretCleared,
      ),
    ).toEqual({ kind: 'SELECTOR_NOT_ELIGIBLE', category: 'REGION' })
  })

  it('refuses a selector value no access grant confers on the caller in this space', () => {
    expect(
      markingRefusal(
        { level: 'OFFICIAL', eyesOnly: [], selectors: [{ category: 'FRUIT', value: 'BANANA' }] },
        secretCleared,
      ),
    ).toEqual({ kind: 'SELECTOR_NOT_GRANTED', category: 'FRUIT', value: 'BANANA' })
  })

  it('claims no grant refusal while the grants are unknown — the server still decides', () => {
    // The grants read may not have answered; a refusal the SPA cannot justify
    // would hide a value from someone entitled to it.
    expect(
      markingRefusal(
        { level: 'OFFICIAL', eyesOnly: [], selectors: [{ category: 'FRUIT', value: 'BANANA' }] },
        { ...secretCleared, selectorGrants: null },
      ),
    ).toBeNull()
  })

  it('still refuses on eligibility while the grants are unknown — eligibility comes from the token, which is known', () => {
    expect(
      markingRefusal(
        { level: 'OFFICIAL', eyesOnly: [], selectors: [{ category: 'REGION', value: 'NORTH' }] },
        { ...secretCleared, selectorGrants: null },
      ),
    ).toEqual({ kind: 'SELECTOR_NOT_ELIGIBLE', category: 'REGION' })
  })

  it('refuses an eyes-only set that excludes the caller, even at a level they hold', () => {
    // §21.6's own example: SECRET US EYES ONLY set by a UK national loses
    // them the page just as completely as over-classifying it would.
    expect(markingRefusal({ level: 'SECRET', eyesOnly: ['US'], selectors: [] }, secretCleared)).toEqual({
      kind: 'EYES_ONLY_EXCLUDES_YOU',
      viewerHasNoNationality: false,
    })
  })

  it('reports the gates in ladder order — level, then eligibility, then grant, then caveat', () => {
    const failsEverything = {
      level: 'TOP_SECRET' as const,
      eyesOnly: ['US'],
      selectors: [
        { category: 'FRUIT', value: 'BANANA' },
        { category: 'REGION', value: 'NORTH' },
      ],
    }
    expect(markingRefusal(failsEverything, secretCleared)?.kind).toBe('ABOVE_CLEARANCE')
    // Level fixed: the first selector fails on grant, but the SECOND fails on
    // eligibility — and eligibility comes before grant per selector, in the
    // order the selectors are listed, so the first selector's grant failure
    // is what the server would name.
    expect(markingRefusal({ ...failsEverything, level: 'SECRET' }, secretCleared)).toEqual({
      kind: 'SELECTOR_NOT_GRANTED',
      category: 'FRUIT',
      value: 'BANANA',
    })
    expect(
      markingRefusal({ ...failsEverything, level: 'SECRET', selectors: [failsEverything.selectors[1]!] }, secretCleared),
    ).toEqual({ kind: 'SELECTOR_NOT_ELIGIBLE', category: 'REGION' })
    // Every selector fine: the caveat is last.
    expect(markingRefusal({ ...failsEverything, level: 'SECRET', selectors: [] }, secretCleared)?.kind).toBe(
      'EYES_ONLY_EXCLUDES_YOU',
    )
  })

  it('distinguishes "you hold no nationality at all" so the copy can say the more useful thing', () => {
    expect(
      markingRefusal({ level: 'OFFICIAL', eyesOnly: ['UK'], selectors: [] }, { ...secretCleared, nationality: [] }),
    ).toEqual({
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
      expect(
        markingRefusal({ level: 'OFFICIAL', eyesOnly: ['NZ'], selectors: [] }, { ...secretCleared, nationality: held }),
      ).toBeNull()
    }
  })

  it('has no input for the UK prefix — §21.12 gives it no access-control semantics', () => {
    // Structural: the draft type has no prefix field, so nothing about a
    // prefix can change a verdict.
    expect(Object.keys({ level: 'SECRET' as ClassificationLevel, eyesOnly: ['UK'], selectors: [] })).not.toContain(
      'ukPrefix',
    )
  })

  it('claims no refusal when the caller is unknown — the server still decides', () => {
    expect(markingRefusal({ level: 'TOP_SECRET', eyesOnly: ['XX'], selectors: [] }, null)).toBeNull()
  })
})

describe('describeMarkingRefusal', () => {
  it('leads a level refusal with the same short reason the option carries', () => {
    expect(describeMarkingRefusal({ kind: 'ABOVE_CLEARANCE', level: 'TOP_SECRET' })).toMatch(
      /^Above your clearance —/,
    )
  })

  it('names the category or the value for the two selector refusals', () => {
    expect(describeMarkingRefusal({ kind: 'SELECTOR_NOT_ELIGIBLE', category: 'FRUIT' })).toBe(
      'Not eligible for FRUIT material, so you could not read this page after marking it.',
    )
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
