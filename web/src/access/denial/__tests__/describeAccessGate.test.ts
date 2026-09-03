import { describe, expect, it } from 'vitest'
import type { AccessGate } from '../../../graphql/generated/graphql'
import { accessGateTitle, describeAccessGate, type GateCheck } from '../describeAccessGate'

const failing = (gate: AccessGate, detail: Partial<GateCheck> = {}): GateCheck => ({ gate, passed: false, ...detail })
const passing = (gate: AccessGate, detail: Partial<GateCheck> = {}): GateCheck => ({ gate, passed: true, ...detail })

/**
 * The sentences a denial (design.md §6.7 / §21.8) and the inspector (§6.6)
 * put beside each gate. Pinned word for word: the help text names them, and
 * a reader is meant to recognise the same sentence on the protected screen,
 * on a tree leaf and in the inspector.
 */
describe('accessGateTitle', () => {
  it('names every gate on the ladder', () => {
    expect(accessGateTitle('SPACE_ACCESS')).toBe('Space access')
    expect(accessGateTitle('CLASSIFICATION')).toBe('Clearance')
    expect(accessGateTitle('SELECTOR_ELIGIBILITY')).toBe('Eligibility')
    expect(accessGateTitle('SELECTOR_GRANT')).toBe('Selector grant')
    expect(accessGateTitle('NATIONAL_CAVEAT')).toBe('National caveat')
    expect(accessGateTitle('RESTRICTION')).toBe('Restriction')
    expect(accessGateTitle('REPLICA')).toBe('Replica')
    expect(accessGateTitle('ROLE')).toBe('Role')
  })

  it('falls back to the wire name for a gate this build has never heard of', () => {
    expect(accessGateTitle('QUANTUM' as AccessGate)).toBe('QUANTUM')
  })
})

describe('describeAccessGate — failing', () => {
  it('says the one sentence a caller with no access grant is told', () => {
    expect(describeAccessGate(failing('SPACE_ACCESS'))).toBe('You have no access to this space.')
  })

  it('names the required clearance, and what the reader holds when that is known', () => {
    expect(
      describeAccessGate(failing('CLASSIFICATION', { requiredLevelName: 'SECRET' }), { heldLevelName: 'OFFICIAL-SENSITIVE' }),
    ).toBe('Needs SECRET clearance; you hold OFFICIAL-SENSITIVE.')
    expect(describeAccessGate(failing('CLASSIFICATION', { requiredLevelName: 'SECRET' }))).toBe('Needs SECRET clearance.')
    // The inspector's rows carry no level detail; the sentence still stands.
    expect(describeAccessGate(failing('CLASSIFICATION'))).toBe('Above your clearance.')
  })

  it('names the category for eligibility and the value for a grant', () => {
    expect(describeAccessGate(failing('SELECTOR_ELIGIBILITY', { category: 'FRUIT' }))).toBe(
      'Not eligible for FRUIT material.',
    )
    expect(describeAccessGate(failing('SELECTOR_GRANT', { category: 'FRUIT', value: 'APPLE' }))).toBe(
      'APPLE is not granted to you in this space.',
    )
    expect(describeAccessGate(failing('SELECTOR_GRANT', { category: 'FRUIT' }))).toBe(
      'No FRUIT value is granted to you in this space.',
    )
  })

  it("lists the caveat's countries as the server's label does, slash-joined and un-reordered", () => {
    expect(describeAccessGate(failing('NATIONAL_CAVEAT', { countries: ['AUS', 'NZ'] }))).toBe('Releasable to AUS/NZ only.')
    expect(describeAccessGate(failing('NATIONAL_CAVEAT', { countries: ['US', 'CAN'] }))).toBe('Releasable to US/CAN only.')
    expect(describeAccessGate(failing('NATIONAL_CAVEAT'))).toBe('Not releasable to your nationality.')
  })

  it('says a restriction blocked it, and where the rule sits, and never its expression or page', () => {
    expect(describeAccessGate(failing('RESTRICTION', { ruleId: 'rule-9' }))).toBe('Blocked by a restriction rule.')
    expect(describeAccessGate(failing('RESTRICTION', { ruleId: 'rule-9', inherited: true }))).toBe(
      'Blocked by a restriction rule on an ancestor page.',
    )
  })

  it('covers the two edit-only gates', () => {
    expect(describeAccessGate(failing('REPLICA'))).toMatch(/read-only replica/)
    expect(describeAccessGate(failing('ROLE', { requiredRole: 'EDITOR' }))).toBe('Needs the Editor role in this space.')
    expect(describeAccessGate(failing('ROLE', { requiredRole: 'SPACE_ADMIN' }))).toBe(
      'Needs the Space admin role in this space.',
    )
    expect(describeAccessGate(failing('ROLE'))).toBe('Needs a role grant in this space.')
  })
})

describe('describeAccessGate — passing (the inspector)', () => {
  it('says what the reader holds, gate by gate', () => {
    expect(describeAccessGate(passing('SPACE_ACCESS'))).toBe('You hold an access grant in this space.')
    expect(
      describeAccessGate(passing('CLASSIFICATION', { requiredLevelName: 'SECRET' }), { heldLevelName: 'TOP SECRET' }),
    ).toBe('SECRET clearance required; you hold TOP SECRET.')
    expect(describeAccessGate(passing('CLASSIFICATION'))).toBe('Your clearance covers this page.')
    expect(describeAccessGate(passing('SELECTOR_ELIGIBILITY', { category: 'FRUIT' }))).toBe('Eligible for FRUIT material.')
    expect(describeAccessGate(passing('SELECTOR_GRANT', { category: 'FRUIT', value: 'APPLE' }))).toBe(
      'APPLE is granted to you in this space.',
    )
    expect(describeAccessGate(passing('NATIONAL_CAVEAT', { countries: ['AUS', 'NZ'] }))).toBe(
      'Releasable to AUS/NZ; you qualify.',
    )
    expect(describeAccessGate(passing('NATIONAL_CAVEAT'))).toBe('No national caveat withholds this page from you.')
    expect(describeAccessGate(passing('RESTRICTION'))).toBe('Every restriction on this page and its ancestors passes.')
    expect(describeAccessGate(passing('REPLICA'))).toBe('This space is not a replica.')
    expect(describeAccessGate(passing('ROLE'))).toBe('You hold a role in this space that allows this.')
  })
})
