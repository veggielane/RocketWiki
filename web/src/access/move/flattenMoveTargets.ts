import { parseRuleNode } from '../ruleSerializer'
import type { MoveTargetOption, RestrictionSummary } from './visibilityChange'

export interface MoveTreeNode {
  id: string
  title: string
  ownViewRestriction?: { ruleId: string; expressionJson: string } | null
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
