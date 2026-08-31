import { describe, expect, it } from 'vitest'
import { describeTimeSince } from '../relativeTime'

/**
 * How long ago something happened, in words.
 *
 * The stale feed is SORTED by this value, so the phrasing is what makes the
 * order legible — a list that reads "3 months / 2 weeks / 5 months" looks
 * broken even when the sort is right. Every case below pins a fixed `now`,
 * because a relative formatter tested against the real clock is a test that
 * means something different on every run.
 */
const NOW = new Date('2026-08-31T12:00:00Z')
const ago = (utcIso: string) => describeTimeSince(utcIso, NOW)

describe('the unit is the largest one that still says something', () => {
  it('reads the recent past in minutes and hours', () => {
    expect(ago('2026-08-31T11:55:00Z')).toBe('5 minutes ago')
    expect(ago('2026-08-31T09:00:00Z')).toBe('3 hours ago')
  })

  it('reads days, and lets the locale say "yesterday"', () => {
    // `numeric: 'auto'` is what turns "1 day ago" into the word people use.
    expect(ago('2026-08-30T12:00:00Z')).toBe('yesterday')
    expect(ago('2026-08-27T12:00:00Z')).toBe('4 days ago')
  })

  it('reads weeks, months and years as the gap widens', () => {
    expect(ago('2026-08-14T12:00:00Z')).toBe('2 weeks ago')
    expect(ago('2026-05-31T12:00:00Z')).toBe('3 months ago')
    expect(ago('2024-08-31T12:00:00Z')).toBe('2 years ago')
  })

  it('never reports a coarser unit than the gap supports', () => {
    // The bug this stops: an off-by-one ladder that calls six days "1 week",
    // which on the stale list would sort below things it reads as older than.
    expect(ago('2026-08-25T12:00:00Z')).toBe('6 days ago')
    expect(ago('2026-08-24T12:00:00Z')).toBe('last week')
  })
})

describe('the ends of the range', () => {
  it('says "just now" rather than counting seconds', () => {
    // A row that ticks 12/13/14 seconds is movement with no information, and
    // these lists do not re-render on a timer — a second-level reading would be
    // stale the moment it was painted.
    expect(ago('2026-08-31T11:59:59Z')).toBe('just now')
    expect(ago('2026-08-31T11:59:01Z')).toBe('just now')
  })

  it('rounds a future timestamp down to "just now" instead of counting forward', () => {
    // A timestamp ahead of `now` is a clock disagreeing with the server, not a
    // fact about the page. "In 2 minutes" on a list of things that have already
    // happened reads as a bug.
    expect(ago('2026-08-31T12:05:00Z')).toBe('just now')
  })

  it('returns nothing at all for a value it cannot read', () => {
    // Empty, so a caller renders no timestamp rather than "Invalid Date ago".
    expect(ago('not a date')).toBe('')
    expect(ago('')).toBe('')
  })
})

describe('it stays a sibling of the absolute formatter, not a replacement', () => {
  it('answers "how long since", which an absolute stamp does not', () => {
    // `formatTimestamp` answers "when"; a feed asks "how stale". Keeping both
    // is the point — the audit log still shows raw UTC on purpose.
    expect(ago('2026-05-31T12:00:00Z')).toBe('3 months ago')
    expect(new Date('2026-05-31T12:00:00Z').toLocaleString()).toContain('2026')
  })
})

describe('the year boundary, which the obvious constant gets wrong', () => {
  it('calls an exact two-year anniversary two years', () => {
    // 730 days over the astronomical year (365.2425) is 1.998, which floors to
    // 1 and prints "last year" for something two years old — and an exact
    // anniversary is the case a reader is most likely to notice.
    expect(ago('2024-08-31T12:00:00Z')).toBe('2 years ago')
  })

  it('leaves no gap between the coarsest month and the finest year', () => {
    // A year of 365 days sits just under twelve average months, so nothing can
    // be too long for months and too short for years and read "12 months ago".
    expect(ago('2025-08-31T12:00:00Z')).toBe('last year')
    expect(ago('2025-09-05T12:00:00Z')).toBe('11 months ago')
  })
})
