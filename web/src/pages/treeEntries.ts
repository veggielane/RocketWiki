/**
 * The page tree's entries are a union (design.md §6.7 / §21.8): a readable
 * `PageTreeNode`, or a `ProtectedTreeNode` — a leaf standing where a page
 * this caller may not read sits, carrying its denial and nothing else (no
 * id, slug, icon, labels or children). The two tree renderers branch on that
 * union themselves; everything ELSE that walks a tree — the move-target
 * flattener, the parent picker, the label filter, the insert-link dialog,
 * the default-page picker — wants only the readable pages, and these helpers
 * are how they get them without each learning the discriminator.
 *
 * Branching on `__typename` rather than on which fields arrived: the server's
 * own discriminator is the one fact that cannot be confused with a field
 * that happens to be missing, and every tree document selects it.
 */

/** The discriminator every protected entry carries. */
export interface ProtectedTreeEntryTag {
  __typename: 'ProtectedTreeNode'
}

interface TreeEntry {
  __typename?: string
}

/** A readable entry of `E`: the union with its protected arm removed. */
export type ReadableEntry<E> = Exclude<E, ProtectedTreeEntryTag>

/** The element type of a `children` property that may be absent, optional or required. */
type ChildOf<E> = E extends { children?: infer C } ? (NonNullable<C> extends readonly (infer I)[] ? I : never) : never

/**
 * `E` with every protected entry removed at every depth. Applied per union
 * member (the conditional distributes), so each readable node keeps its own
 * fields and only its `children` are re-typed — as optional, which is how
 * every consumer of a tree declares them, and which a node the document
 * stopped short of satisfies by leaving them out.
 */
export type ReadableTree<E> = E extends ProtectedTreeEntryTag
  ? never
  : 'children' extends keyof E
    ? Omit<E, 'children'> & { children?: ReadableTree<ChildOf<E>>[] }
    : E

export function isProtectedEntry<E extends TreeEntry>(entry: E): entry is E & ProtectedTreeEntryTag {
  return entry.__typename === 'ProtectedTreeNode'
}

/** The readable entries of one level, in order. Shallow: their children are left as they came. */
export function readableNodes<E extends TreeEntry>(entries: readonly E[]): ReadableEntry<E>[] {
  return entries.filter((entry): entry is ReadableEntry<E> => !isProtectedEntry(entry))
}

/**
 * The readable tree: protected entries dropped at every depth. A placeholder
 * is a leaf by construction (the server never descends into a denied page),
 * so dropping it loses nothing readable beneath it.
 */
export function readableTree<E extends TreeEntry>(entries: readonly E[]): ReadableTree<E>[] {
  const out: ReadableTree<E>[] = []
  for (const entry of entries) {
    if (isProtectedEntry(entry)) continue
    const children = (entry as { children?: readonly TreeEntry[] }).children
    out.push((children ? { ...entry, children: readableTree(children) } : entry) as ReadableTree<E>)
  }
  return out
}
