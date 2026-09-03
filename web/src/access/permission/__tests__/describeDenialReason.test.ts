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

  it('describes the classification token with the level it carries', () => {
    expect(describeDenialReason('classification:SECRET')).toMatch(/SECRET/)
    expect(describeDenialReason('classification:SECRET')).toMatch(/above this user's clearance/)
  })

  it('describes the three selector tokens by category', () => {
    expect(describeDenialReason('selector:not_eligible:FRUIT')).toBe('This user is not eligible for FRUIT material.')
    expect(describeDenialReason('selector:unknown:FRUIT')).toMatch(/FRUIT.*does not configure.*nobody/)
    expect(describeDenialReason('selector:not_granted:FRUIT')).toMatch(/No access grant.*FRUIT value/)
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
