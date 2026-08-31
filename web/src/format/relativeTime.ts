/**
 * "3 months ago" — how long since something happened.
 *
 * A sibling of `formatTimestamp`, not a replacement for it. An absolute
 * timestamp answers "when did this happen"; a feed asks "how stale is this",
 * and `2026-04-14 09:12:03` does not answer that without the reader doing
 * arithmetic. The homepage's three feeds are the surfaces that want it: the
 * stale list is *sorted* by this, so the phrasing is what makes the order
 * legible from the list itself.
 *
 * Deliberately NOT a generalisation of `trash/trashCountdown.ts`. That one
 * counts down to a fixed 30-day expiry and speaks in a domain voice ("Expires
 * in 3 days"); this counts up from an arbitrary past instant. Merging them
 * would give one function two jobs and a threshold argument nobody could read.
 *
 * Uses `Intl.RelativeTimeFormat` for the same reason `formatTimestamp` uses
 * `toLocaleString` — pluralisation, wording and locale are the browser's to
 * decide, not ours to hand-roll in English. `numeric: 'auto'` is what turns
 * "1 day ago" into "yesterday".
 */

const MINUTE = 60
const HOUR = MINUTE * 60
const DAY = HOUR * 24
const WEEK = DAY * 7
/** The average civil month, which is what "5 months ago" already means colloquially. */
const MONTH = DAY * 30.436875
/**
 * 365, not the astronomical 365.2425 — and the quarter-day matters.
 *
 * Two calendar years is 730 days, which divided by 365.2425 is 1.998, floors to
 * 1, and prints "last year" for something two years old. An exact anniversary
 * is the case a reader is most likely to notice being wrong. 365 also stays
 * just under twelve average months (365.24), so there is no gap where a span
 * is too long for months and too short for years and reads "12 months ago".
 */
const YEAR = DAY * 365

/** Largest unit that still gives a number worth reading, coarsest last. */
const LADDER: { seconds: number; unit: Intl.RelativeTimeFormatUnit }[] = [
  { seconds: YEAR, unit: 'year' },
  { seconds: MONTH, unit: 'month' },
  { seconds: WEEK, unit: 'week' },
  { seconds: DAY, unit: 'day' },
  { seconds: HOUR, unit: 'hour' },
  { seconds: MINUTE, unit: 'minute' },
]

/**
 * How long ago `utcIso` was, in the viewer's own language.
 *
 * `now` is injectable so tests can pin it: a relative formatter tested against
 * the real clock is a test that means something different every time it runs.
 *
 * Anything under a minute is "just now" rather than a second count — a feed row
 * that ticks 12/13/14 seconds ago is movement with no information in it, and
 * these lists do not re-render on a timer anyway, so a second-level reading
 * would be stale the moment it was painted.
 */
export function describeTimeSince(utcIso: string, now: Date = new Date()): string {
  const then = new Date(utcIso)
  if (Number.isNaN(then.getTime())) return ''

  const secondsAgo = (now.getTime() - then.getTime()) / 1000
  // A timestamp in the future is a clock disagreeing with the server, not a
  // fact about the page. "In 2 minutes" on a list of things that have already
  // happened reads as a bug; "just now" is the honest rounding.
  if (secondsAgo < MINUTE) return 'just now'

  // Anything that reaches here is at least a minute old, so a rung always
  // matches; the fallback exists to satisfy the type, not as a second copy of
  // the "just now" rule above. Two independent lines both handling the
  // future-timestamp case would mean neither could be tested — breaking either
  // one alone left the other quietly covering for it.
  const rung = LADDER.find(({ seconds }) => secondsAgo >= seconds) ?? LADDER[LADDER.length - 1]
  return format(-Math.floor(secondsAgo / rung.seconds), rung.unit)
}

function format(value: number, unit: Intl.RelativeTimeFormatUnit): string {
  return new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' }).format(value, unit)
}
