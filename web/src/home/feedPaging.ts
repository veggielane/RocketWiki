/**
 * How much of a feed one request may ask for.
 *
 * Its own module so the numbers can be asserted against `schema.graphql`
 * directly — see `pagedFieldLimits.test.ts`. The audit log shipped a page that
 * never loaded because the SPA asked a field for more than its cap and nothing
 * compared the two; these three feeds are checked the same way.
 */

/** The first page, and the step "Show more" adds. */
export const FEED_PAGE_SIZE = 10

/**
 * The connections' `MaxPageSize`, published on the schema as
 * `@listSize(assumedSize: 20)`.
 *
 * Exceeding it is an ERROR at validation, not a clamp, so the ceiling has to be
 * respected on this side. It is also a COST ceiling as much as a row one: each
 * row costs page-size × object-fields, and 20 is what the pinned feed selection
 * affords — which is why the rows select `page { … marking }` (plus `author` on
 * the activity feed) and nothing more.
 */
export const FEED_MAX_PAGE_SIZE = 20
