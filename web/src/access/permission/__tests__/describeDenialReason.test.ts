import { describe, expect, it } from 'vitest'
import { describeDenialReason } from '../describeDenialReason'

/**
 * The audit's first-failing-gate tokens (design.md §6.4's ladder), one
 * sentence each. Pinned word for word for the two tokens the help text
 * names; the rest by the fact they carry.
 */
describe('describeDenialReason', () => {
  it('returns null for null (no denial)', () => {
    expect(describeDenialReason(null)).toBeNull()
  })

  it('describes no-space-access — no ACCESS grant matched', () => {
    expect(describeDenialReason('no-space-access')).toBe('No access grant in this space matches this user.')
  })

  it('describes replica-read-only', () => {
    expect(describeDenialReason('replica-read-only')).toMatch(/read-only replica/i)
  })

  it('describes insufficient-space-role in terms of the two roles that exist', () => {
    expect(describeDenialReason('insufficient-space-role')).toBe(
      'No role grant gives this user Editor or Space admin in this space.',
    )
    expect(describeDenialReason('insufficient-space-role')).not.toMatch(/viewer/i)
  })

  it('describes marking:unavailable as a fact about the page, not about the user', () => {
    expect(describeDenialReason('marking:unavailable')).toBe(
      "This page's marking is missing, so nobody can read it until it is restored.",
    )
  })

  it('describes the two selector tokens by category', () => {
    expect(describeDenialReason('selector:unknown:FRUIT')).toMatch(/FRUIT.*does not configure.*nobody/)
    expect(describeDenialReason('selector:not_granted:FRUIT')).toMatch(/No access grant.*FRUIT value/)
  })

  it('shows the retired classification and eligibility tokens raw — neither is minted any more', () => {
    // An old audit row could still carry one. The raw token is the honest
    // rendering of a reason the ladder no longer has; a sentence about a
    // clearance would describe a check this deployment does not make.
    expect(describeDenialReason('classification:SECRET')).toBe('classification:SECRET')
    expect(describeDenialReason('selector:not_eligible:FRUIT')).toBe('selector:not_eligible:FRUIT')
  })

  it('describes the caveat token', () => {
    expect(describeDenialReason('caveat:eyes_only')).toMatch(/national caveat.*nationalities/)
  })

  it('describes a restriction:{pageId}:{ruleId} code', () => {
    expect(describeDenialReason('restriction:page-1:rule-1')).toMatch(/page-1/)
  })

  it('falls back to the raw string for an unrecognized code, including the retired no-space-role', () => {
    expect(describeDenialReason('something-new')).toBe('something-new')
    expect(describeDenialReason('no-space-role')).toBe('no-space-role')
  })
})
