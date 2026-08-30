/**
 * How a question measures up against the instance's own limit.
 *
 * The limit is `assistantStatus.maxQuestionChars` — per-instance configuration,
 * not a constant this app may assume. Everything here takes it as an argument
 * and says nothing at all when it is absent, which is the honest answer for an
 * unconfigured assistant or a status query that failed: a warning threshold
 * invented by the client would be wrong on every instance configured lower, and
 * wrong in the direction that matters, staying quiet at a length the server
 * refuses.
 *
 * **The server's comparison is `question.Length > MaxQuestionChars`**, so a
 * question exactly AT the limit is accepted. `over` therefore starts one
 * character later, and the boundary is pinned from both sides in the tests.
 */
export type QuestionLengthState =
  /** No limit known — the counter says nothing rather than guessing one. */
  | { kind: 'unknown' }
  /** Far enough from the limit that a counter would be noise. */
  | { kind: 'ok' }
  | { kind: 'approaching'; remaining: number; limit: number }
  | { kind: 'over'; over: number; limit: number }

/**
 * The last tenth of the allowance, and never fewer than one character.
 *
 * A fraction rather than a fixed count, because the limit is configurable: 200
 * characters of warning is generous against a 2,000 limit and absurd against a
 * 300 one.
 */
export function warnFrom(limit: number): number {
  return limit - Math.max(Math.ceil(limit * 0.1), 1)
}

/**
 * `length` must be the length of what will actually be SENT — the composer
 * trims before asking, so an untrimmed count would nag about whitespace the
 * server never sees.
 *
 * Both sides count UTF-16 code units (JS `.length`, .NET `string.Length`), so
 * they agree exactly, including for astral characters. Counting code points
 * here (`[...text].length`) would quietly disagree with the server for any
 * question containing an emoji.
 */
export function describeQuestionLength(length: number, limit: number | null | undefined): QuestionLengthState {
  if (typeof limit !== 'number' || !Number.isFinite(limit) || limit <= 0) return { kind: 'unknown' }
  if (length > limit) return { kind: 'over', over: length - limit, limit }
  if (length > warnFrom(limit)) return { kind: 'approaching', remaining: limit - length, limit }
  return { kind: 'ok' }
}

/**
 * The counter's words, or `null` when there is nothing worth saying.
 *
 * Never colour alone (WCAG 1.4.1): "over the limit" and "characters left" are
 * different sentences, not the same sentence in two colours.
 */
export function questionLengthMessage(state: QuestionLengthState): string | null {
  switch (state.kind) {
    case 'unknown':
    case 'ok':
      return null
    case 'approaching':
      return `${state.remaining.toLocaleString()} character${state.remaining === 1 ? '' : 's'} left of ${state.limit.toLocaleString()}.`
    case 'over':
      // Says what will happen, in the same voice as the refusal itself: the
      // server rejects rather than trims, so the choice of what to cut stays
      // with the person who wrote it.
      return `${state.over.toLocaleString()} character${state.over === 1 ? '' : 's'} over the ${state.limit.toLocaleString()} this wiki accepts — it will be refused, not shortened.`
  }
}
