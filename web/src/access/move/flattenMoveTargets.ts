import { parseRuleNode } from '../ruleSerializer'
import type { MoveTargetOption, RestrictionSummary } from './visibilityChange'

export interface MoveTreeNode {
  id: string
  title: string
  /**
   * NOTE (schema reconciliation): the real PageTreeNode carries no
   * restriction data, so this is never populated from the API today — the
   * accumulation logic is kept (and still tested) for the day the API
   * exposes it again; callers must treat "no restrictions anywhere" as
   * "restriction data unavailable", not "no restrictions exist" (see
   * MovePageDialog's restrictionDataUnavailable prop).
   */
  ownViewRestriction?: { ruleId: string; expressionJson: string } | null
  sortOrder?: number
  children?: MoveTreeNode[]
}

/**
 * Flattens a page tree into a list of candidate move targets, each carrying
 * the *accumulated* view-restriction chain a page would inherit if moved
 * there (design.md §6.4: restrictions accumulate down the tree — moving
 * under node X means inheriting X's own restriction plus everything above
 * it). The page being moved, and its entire subtree, is excluded — you
 * can't move a page into its own descendant.
 */
export function flattenMoveTargets(roots: MoveTreeNode[], excludePageId: string): MoveTargetOption[] {
  const options: MoveTargetOption[] = [{ id: null, title: '(space root)', ancestorRestrictions: [] }]

  function walk(node: MoveTreeNode, ancestorChainExcludingThis: RestrictionSummary[]): void {
    if (node.id === excludePageId) {
      return
    }

    const ownRestriction: RestrictionSummary[] = node.ownViewRestriction
      ? [
          {
            ruleId: node.ownViewRestriction.ruleId,
            pageId: node.id,
            pageTitle: node.title,
            action: 'view',
            expression: parseRuleNode(node.ownViewRestriction.expressionJson),
          },
        ]
      : []
    const chainIncludingThis = [...ancestorChainExcludingThis, ...ownRestriction]

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
