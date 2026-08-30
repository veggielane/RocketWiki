/**
 * How much of the account roster one request may ask for.
 *
 * Its own module so the numbers can be asserted directly — including against
 * `schema.graphql`, which is the point. `auditEvents` shipped a page that never
 * loaded because the SPA asked for 100 from a field capped at 50, and nothing
 * anywhere compared the two. A drift guard is cheap; that incident was not.
 */

/** The step "Show more" adds. */
export const PAGE_SIZE = 25

/**
 * The server's own ceiling on `first`, mirrored here so the screen never asks
 * for a page it would be refused.
 *
 * It is the field's `MaxPageSize`, published on the schema as
 * `@listSize(assumedSize: …)` — the same cap `auditEvents` carries. Unlike the
 * search connection, which clamps a too-large `first` down and answers anyway,
 * exceeding this one is an ERROR at validation, so the clamp has to happen on
 * this side.
 */
export const MAX_PAGE_SIZE = 100

/**
 * How many rows the URL says are on screen.
 *
 * Clamped rather than validated: a stale or hand-edited `?show=` is still a
 * link somebody followed, and the honest response is to show them the nearest
 * page that exists rather than an error — or, worse, to forward the number
 * unchecked to a connection that refuses it.
 */
export function shownFromParams(raw: string | null): number {
  const parsed = Number(raw)
  if (!Number.isFinite(parsed)) return PAGE_SIZE
  return Math.min(Math.max(Math.trunc(parsed), PAGE_SIZE), MAX_PAGE_SIZE)
}
