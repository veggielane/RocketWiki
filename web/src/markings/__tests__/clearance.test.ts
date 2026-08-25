import { describe, expect, it } from 'vitest'
import type { ClassificationLevel } from '../../graphql/generated/graphql'
import {
  CLASSIFICATION_LADDER,
  canonicalCountries,
  canonicalCountry,
  canonicalPrefix,
  clearanceRank,
  describeMarkingRefusal,
  eyesOnlyAdmits,
  levelIsWithinClearance,
  markingLevelRank,
  markingRefusal,
} from '../clearance'

/**
 * design.md §21's comparison, client-side. These tests are the reason the
 * marking control is allowed to grey anything out: the UI is only entitled to
 * prevent what the server would actually refuse, so this suite pins the
 * mirror to `ClearanceGate`'s rules rather than to what looked reasonable.
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

  it('ranks an unplaceable CLEARANCE at OFFICIAL, the only thing "nothing" is worth', () => {
    expect(clearanceRank('COSMIC' as ClassificationLevel)).toBe(clearanceRank('OFFICIAL'))
  })

  it('never lets an unplaceable clearance reach an unplaceable level', () => {
    expect(levelIsWithinClearance('COSMIC' as ClassificationLevel, 'COSMIC' as ClassificationLevel)).toBe(false)
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

  it('collapses null, empty and whitespace prefixes to one no-prefix state (§21.12)', () => {
    expect(canonicalPrefix(null)).toBeNull()
    expect(canonicalPrefix('')).toBeNull()
    expect(canonicalPrefix('   ')).toBeNull()
    expect(canonicalPrefix(' uk ')).toBe('UK')
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

const secretCleared = { clearance: 'SECRET' as ClassificationLevel, nationality: ['UK'] }

describe('markingRefusal — §21.6 mirrored so the UI prevents rather than refuses', () => {
  it('finds nothing wrong with a marking the caller could still read', () => {
    expect(markingRefusal({ level: 'SECRET', eyesOnly: ['UK', 'US'] }, secretCleared)).toBeNull()
  })

  it('refuses a level above the caller clearance', () => {
    expect(markingRefusal({ level: 'TOP_SECRET', eyesOnly: [] }, secretCleared)).toEqual({
      kind: 'ABOVE_CLEARANCE',
      level: 'TOP_SECRET',
    })
  })

  it('refuses an eyes-only set that excludes the caller, even at a level they hold', () => {
    // §21.6's own example: SECRET [US EYES ONLY] set by a GB national loses
    // them the page just as completely as over-classifying it would.
    expect(markingRefusal({ level: 'SECRET', eyesOnly: ['US'] }, secretCleared)).toEqual({
      kind: 'EYES_ONLY_EXCLUDES_YOU',
      viewerHasNoNationality: false,
    })
  })

  it('reports the level first when both halves fail, matching the gate order', () => {
    expect(markingRefusal({ level: 'TOP_SECRET', eyesOnly: ['US'] }, secretCleared)?.kind).toBe('ABOVE_CLEARANCE')
  })

  it('distinguishes "you hold no nationality at all" so the copy can say the more useful thing', () => {
    expect(markingRefusal({ level: 'OFFICIAL', eyesOnly: ['UK'] }, { clearance: 'SECRET', nationality: [] })).toEqual({
      kind: 'EYES_ONLY_EXCLUDES_YOU',
      viewerHasNoNationality: true,
    })
  })

  it('survives a nationality that arrives in any case or padding — the comparison canonicalizes both sides', () => {
    // This is the §21.4 case-mismatch trap one layer up, and it is worth its
    // own test at THIS level rather than only on eyesOnlyAdmits: a marking's
    // country set is always canonical, so a principal whose token says `gb`
    // IS admitted to a `GB` marking by the server. A comparison that folded
    // only one side would conclude the opposite and warn an author out of a
    // marking that would have worked — a false refusal in the affordance whose
    // entire purpose is to predict the server's answer.
    //
    // It also guards a tempting future simplification: "the server already
    // canonicalizes `me.nationality`, so we can drop ours". The wire value is
    // not this module's to assume, and the cost of being wrong is silent.
    for (const held of [['gb'], [' GB '], ['Gb']]) {
      expect(markingRefusal({ level: 'OFFICIAL', eyesOnly: ['GB'] }, { clearance: 'SECRET', nationality: held })).toBeNull()
    }
  })

  it('ignores the prefix entirely — §21.12 gives it no access-control semantics', () => {
    // The draft type has no prefix field at all, which is the structural half
    // of this; the behavioural half is that nothing about a prefix can change
    // a verdict, so a marking that passes still passes whatever is written in
    // front of it.
    expect(markingRefusal({ level: 'SECRET', eyesOnly: ['UK'] }, secretCleared)).toBeNull()
    expect(Object.keys({ level: 'SECRET' as ClassificationLevel, eyesOnly: ['UK'] })).not.toContain('prefix')
  })

  it('claims no refusal when the caller is unknown — the server still decides', () => {
    expect(markingRefusal({ level: 'TOP_SECRET', eyesOnly: ['XX'] }, null)).toBeNull()
  })
})

describe('describeMarkingRefusal', () => {
  it('leads a level refusal with the same short reason the option carries', () => {
    expect(describeMarkingRefusal({ kind: 'ABOVE_CLEARANCE', level: 'TOP_SECRET' })).toMatch(
      /^Above your clearance —/,
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
