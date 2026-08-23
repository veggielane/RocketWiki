export interface LabeledTreeNode {
  id: string
  title: string
  labels: string[]
  children?: LabeledTreeNode[]
}

export interface LabelMatch {
  id: string
  title: string
  /** Ancestor titles, root-first, not including the matching page itself — for a breadcrumb. */
  path: string[]
}

/**
 * NOTE (schema reconciliation): currently unmounted — the real
 * PageTreeNode carries no `labels` field (reported contract gap), so the
 * space browser's label-filter facet lost its data source. Kept, with its
 * tests, for the day the field lands.
 *
 * Flattens a space's page tree to just the pages carrying a given label,
 * each with a breadcrumb of its ancestor titles. Flattened rather than
 * returned as a pruned sub-tree: "filter by label" reads as a result list
 * (like search), and a partial tree missing everything between a matching
 * descendant and the root would be a confusing shape to render — its
 * ancestors' *titles* are still useful context, so they travel as a
 * breadcrumb instead.
 */
export function filterTreeByLabel(tree: LabeledTreeNode[], label: string): LabelMatch[] {
  const matches: LabelMatch[] = []

  function walk(nodes: LabeledTreeNode[], path: string[]): void {
    for (const node of nodes) {
      if (node.labels.includes(label)) {
        matches.push({ id: node.id, title: node.title, path })
      }
      if (node.children && node.children.length > 0) {
        walk(node.children, [...path, node.title])
      }
    }
  }

  walk(tree, [])
  return matches
}
