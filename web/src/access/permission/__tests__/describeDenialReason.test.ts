import { describe, expect, it } from 'vitest'
import { describeDenialReason } from '../describeDenialReason'

describe('describeDenialReason', () => {
  it('returns null for null (no denial)', () => {
    expect(describeDenialReason(null)).toBeNull()
  })

  it('describes no-space-role', () => {
    expect(describeDenialReason('no-space-role')).toMatch(/no role/i)
  })

  it('describes replica-read-only', () => {
    expect(describeDenialReason('replica-read-only')).toMatch(/read-only replica/i)
  })

  it('describes insufficient-space-role', () => {
    expect(describeDenialReason('insufficient-space-role')).toMatch(/viewer/i)
  })

  it('describes a restriction:{pageId}:{ruleId} code', () => {
    expect(describeDenialReason('restriction:page-1:rule-1')).toMatch(/page-1/)
  })

  it('falls back to the raw string for an unrecognized code', () => {
    expect(describeDenialReason('something-new')).toBe('something-new')
  })
})
