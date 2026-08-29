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
 * `detachLabel` used to be a gap here and no longer is. It returned only a
 * scalar id, so its response carried no object type for the cache to key on and
 * no list here could catch it — removing a label left a stale chip until
 * something else refetched. Its payload now returns the `PageLabel` it removed,
 * symmetric with attach, which is why that entry below covers both directions.
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
