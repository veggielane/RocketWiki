import { tryParseRuleNode } from '../ruleSerializer'
import type { MoveTargetOption, RestrictionSummary } from './visibilityChange'

/** Mirrors `PageTreeRestriction` — the tree exposes each page's own view restrictions as a LIST of {ruleId, expressionJson}. */
export interface MoveTreeRestriction {
  ruleId: string
  expressionJson: string
}

export interface MoveTreeNode {
  id: string
  title: string
  /** The node's own view restrictions (`PageTreeNode.ownViewRestrictions`) — a list; a page can carry several. */
  ownViewRestrictions?: MoveTreeRestriction[]
  sortOrder?: number
  children?: MoveTreeNode[]
}

function ownRestrictionsOf(node: MoveTreeNode): RestrictionSummary[] {
  return (node.ownViewRestrictions ?? []).map((restriction) => ({
    ruleId: restriction.ruleId,
    pageId: node.id,
    pageTitle: node.title,
    action: 'view',
    // A malformed stored expression must not crash the dialog — the rule
    // still applies (fails closed, design.md §6.3), it just renders as
    // unreadable rather than as a summary.
    expression: tryParseRuleNode(restriction.expressionJson).node,
  }))
}

/**
 * Flattens a page tree into a list of candidate move targets, each carrying
 * the *accumulated* view-restriction chain a page would inherit if moved
 * there (design.md §6.4: restrictions accumulate down the tree — moving
 * under node X means inheriting X's own restrictions plus everything above
 * it). The page being moved, and its entire subtree, is excluded — you
 * can't move a page into its own descendant.
 */
export function flattenMoveTargets(roots: MoveTreeNode[], excludePageId: string): MoveTargetOption[] {
  const options: MoveTargetOption[] = [{ id: null, title: '(space root)', ancestorRestrictions: [] }]

  function walk(node: MoveTreeNode, ancestorChainExcludingThis: RestrictionSummary[]): void {
    if (node.id === excludePageId) {
      return
    }

    const chainIncludingThis = [...ancestorChainExcludingThis, ...ownRestrictionsOf(node)]

    options.push({ id: node.id, title: node.title, ancestorRestrictions: chainIncludingThis })

    for (const child of node.children ?? []) {
      walk(child, chainIncludingThis)
    }
  }

  for (const root of roots) {
    walk(root, [])
  }

  return options
}

/**
 * The view restrictions a page currently inherits from its ancestor chain —
 * the "before" side of `computeVisibilityChange`. The page's *own*
 * restrictions are excluded: they travel with it on a move and never change
 * (design.md §6.4). Returns [] for a page at the space root, and for a page
 * not found in the (depth-limited) tree.
 */
export function ancestorRestrictionsOf(roots: MoveTreeNode[], pageId: string): RestrictionSummary[] {
  function walk(nodes: MoveTreeNode[], chain: RestrictionSummary[]): RestrictionSummary[] | null {
    for (const node of nodes) {
      if (node.id === pageId) {
        return chain
      }
      const found = walk(node.children ?? [], [...chain, ...ownRestrictionsOf(node)])
      if (found) {
        return found
      }
    }
    return null
  }

  return walk(roots, []) ?? []
}

/**
 * The sort position an appended child would get under each candidate parent
 * (`null` key = the space root): max existing child sortOrder + 1, or 0 for
 * a childless target. The real `movePage` mutation requires an explicit
 * `newSortOrder`; "append at the end" is the only placement the move dialog
 * offers, so this is computed here from the same tree the targets came from.
 */
export function nextSortOrderByTarget(roots: MoveTreeNode[]): Map<string | null, number> {
  const result = new Map<string | null, number>()

  function nextAmong(children: MoveTreeNode[] | undefined): number {
    if (!children || children.length === 0) return 0
    return Math.max(...children.map((c, i) => c.sortOrder ?? i)) + 1
  }

  result.set(null, nextAmong(roots))

  function walk(node: MoveTreeNode): void {
    result.set(node.id, nextAmong(node.children))
    for (const child of node.children ?? []) {
      walk(child)
    }
  }

  for (const root of roots) {
    walk(root)
  }

  return result
}
