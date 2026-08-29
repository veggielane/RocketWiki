/**
 * Flattens a space's page tree into the parent choices a new page can be created
 * under, in document order, each carrying its depth so the picker can indent.
 *
 * Deliberately not `flattenMoveTargets`: that one answers a different question.
 * It excludes the moving page's own subtree (you cannot move a page into its own
 * descendant) and computes the accumulated view-restriction chain each target
 * would impose — neither of which applies to a page that does not exist yet.
 * Sharing it would mean a change to move semantics silently changing what you
 * can create under.
 */

export interface ParentTreeNode {
  id: string
  title: string
  children?: ParentTreeNode[]
}

export interface ParentOption {
  /** null is the space root — a top-level page. */
  id: string | null
  title: string
  depth: number
}

export const SPACE_ROOT_OPTION: ParentOption = { id: null, title: '(top level)', depth: 0 }

export function flattenParentOptions(roots: ParentTreeNode[]): ParentOption[] {
  const options: ParentOption[] = [SPACE_ROOT_OPTION]

  function walk(node: ParentTreeNode, depth: number): void {
    options.push({ id: node.id, title: node.title, depth })
    for (const child of node.children ?? []) {
      walk(child, depth + 1)
    }
  }

  for (const root of roots) {
    walk(root, 1)
  }

  return options
}
