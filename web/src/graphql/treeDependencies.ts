/**
 * What the page tree actually depends on, declared for urql's cache.
 *
 * urql's default `cacheExchange` is a DOCUMENT cache: after a mutation it
 * invalidates the cached queries whose results contained one of the
 * `__typename`s the mutation returned. `SpacePageTree` returns `PageTreeNode`,
 * and no mutation in the schema returns one — a created page comes back as
 * `Page`, a delete as `PageDeleteSummary`, a label attach as `PageLabel`. The
 * sets never intersect, so nothing could ever invalidate the tree and the
 * sidebar kept serving the tree it had cached on arrival: create a page and it
 * simply was not there.
 *
 * `additionalTypenames` is urql's documented answer for exactly this — a query
 * declaring the types it depends on but does not select. Listed rather than
 * blanket-refetching so the tree reloads when something changed it, not on
 * every mutation anywhere in the app.
 *
 * Each entry earns its place from something the tree renders:
 * `Page` covers create, move, and the title a content save changes;
 * the two summaries cover delete and restore, which return no `Page` at all;
 * `PageLabel` and `PageMarkingView` cover the label chips and the
 * classification badge on every node.
 *
 * KNOWN GAP, not an oversight: `detachLabel` returns only a scalar id and an
 * error, so its response carries no object type for the cache to key on. No
 * `additionalTypenames` list can catch it, here or anywhere else — removing a
 * label leaves a stale chip until something else refetches. Fixing it means
 * changing that mutation's payload shape, which is a schema change and a
 * separate decision.
 */
export const PAGE_TREE_DEPENDENCIES = [
  'Page',
  'PageDeleteSummary',
  'PageRestoreSummary',
  'PageLabel',
  'PageMarkingView',
] as const

/** Spread into a query's `context` — `useSpacePageTreeQuery({ ..., context: PAGE_TREE_CONTEXT })`. */
export const PAGE_TREE_CONTEXT = {
  additionalTypenames: PAGE_TREE_DEPENDENCIES as unknown as string[],
}
