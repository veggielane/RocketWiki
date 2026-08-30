import { describe, expect, it } from 'vitest'
import { describeQuestionLength, questionLengthMessage, warnFrom } from '../askQuestionLength'

/**
 * The counter's arithmetic, driven from a reported limit rather than a constant.
 *
 * The property that matters is one-directional: **the counter must never read
 * comfortable at a length the server will refuse.** The server's own test is
 * `question.Length > MaxQuestionChars`, so exactly AT the limit is accepted and
 * `over` starts one character later. Both sides of that edge are asserted, for
 * several limits, so nothing here can quietly agree with 2,000 alone.
 */

/** Limits worth checking: the default, one far below it, one far above, and a tiny one. */
const LIMITS = [2000, 350, 8000, 12, 1]

describe('the boundary sits exactly where the server puts it', () => {
  for (const limit of LIMITS) {
    it(`accepts a question of exactly ${limit} and refuses ${limit + 1}`, () => {
      // Below the edge: never 'over'.
      expect(describeQuestionLength(limit, limit).kind).not.toBe('over')
      expect(describeQuestionLength(limit - 1, limit).kind).not.toBe('over')
      // Above it: always 'over', and by the right amount.
      expect(describeQuestionLength(limit + 1, limit)).toEqual({ kind: 'over', over: 1, limit })
      expect(describeQuestionLength(limit + 500, limit)).toEqual({ kind: 'over', over: 500, limit })
    })

    it(`never reads 'ok' anywhere the server would refuse, at a limit of ${limit}`, () => {
      // The one-directional property, swept rather than sampled: from the
      // warning threshold to well past the limit, no length may come back 'ok'.
      for (let length = warnFrom(limit) + 1; length <= limit + 3; length++) {
        expect(describeQuestionLength(length, limit).kind).not.toBe('ok')
      }
    })
  }
})

describe('what the counter says, and when it says nothing', () => {
  it('stays quiet well inside the limit — a counter that is always on is noise', () => {
    expect(describeQuestionLength(10, 2000)).toEqual({ kind: 'ok' })
    expect(questionLengthMessage(describeQuestionLength(10, 2000))).toBeNull()
  })

  it('starts warning in the last tenth of the allowance', () => {
    expect(describeQuestionLength(1800, 2000).kind).toBe('ok')
    expect(describeQuestionLength(1801, 2000)).toEqual({ kind: 'approaching', remaining: 199, limit: 2000 })
  })

  it('scales the warning window with the limit, rather than using a fixed count', () => {
    // 200 characters of warning is generous against 2,000 and absurd against
    // 300, so the threshold is a fraction.
    expect(warnFrom(2000)).toBe(1800)
    expect(warnFrom(300)).toBe(270)
  })

  it('always leaves at least one character of warning, however small the limit', () => {
    expect(warnFrom(1)).toBe(0)
    expect(describeQuestionLength(1, 1)).toEqual({ kind: 'approaching', remaining: 0, limit: 1 })
  })

  it('says how far over, and that the question will be refused rather than trimmed', () => {
    const message = questionLengthMessage(describeQuestionLength(2100, 2000))
    expect(message).toContain('100 characters over the 2,000 this wiki accepts')
    expect(message).toContain('refused, not shortened')
  })

  it('counts in words, not only in colour', () => {
    // WCAG 1.4.1: "over the limit" and "characters left" are different
    // sentences, not one sentence in two colours.
    expect(questionLengthMessage(describeQuestionLength(1900, 2000))).toContain('characters left')
    expect(questionLengthMessage(describeQuestionLength(2100, 2000))).toContain('over')
  })

  it('gets the singular right at one character', () => {
    expect(questionLengthMessage(describeQuestionLength(2001, 2000))).toContain('1 character over')
    expect(questionLengthMessage(describeQuestionLength(1999, 2000))).toContain('1 character left')
  })
})

describe('with no limit reported, the counter says nothing at all', () => {
  // Unconfigured assistant, anonymous caller, failed status query: all null.
  // A threshold invented here would be wrong on every instance configured
  // lower — and wrong in the direction that matters, staying quiet at a length
  // the server refuses.
  for (const absent of [null, undefined, 0, -1, Number.NaN]) {
    it(`treats ${String(absent)} as "no limit known"`, () => {
      expect(describeQuestionLength(999999, absent)).toEqual({ kind: 'unknown' })
      expect(questionLengthMessage(describeQuestionLength(999999, absent))).toBeNull()
    })
  }
})
