import { describe, expect, it } from 'vitest'
import { daysUntil, describeExpiry } from '../trashCountdown'

const NOW = new Date('2026-01-01T00:00:00Z')

describe('daysUntil', () => {
  it('rounds up a partial day remaining', () => {
    // 12 hours away rounds up to 1 full day remaining, not 0.
    expect(daysUntil('2026-01-01T12:00:00Z', NOW)).toBe(1)
  })

  it('returns exactly 30 for a fresh 30-day window', () => {
    expect(daysUntil('2026-01-31T00:00:00Z', NOW)).toBe(30)
  })

  it('returns 0 or negative once past expiry', () => {
    expect(daysUntil('2025-12-31T00:00:00Z', NOW)).toBeLessThanOrEqual(0)
  })
})

describe('describeExpiry', () => {
  it('says "Expired" once past the window', () => {
    expect(describeExpiry('2025-12-01T00:00:00Z', NOW)).toBe('Expired')
  })

  it('uses singular "day" for exactly one day left', () => {
    expect(describeExpiry('2026-01-01T23:00:00Z', NOW)).toBe('Expires in 1 day')
  })

  it('uses plural "days" otherwise', () => {
    expect(describeExpiry('2026-01-10T00:00:00Z', NOW)).toBe('Expires in 9 days')
  })
})
