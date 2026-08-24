import { describeBlockedSubtree } from '../feedback/blockedSubtreeCopy'

/**
 * design.md §6.4.1/§6.7: a refused subtree delete reports only a COUNT of
 * blocked descendants, never their identities — naming them would confirm
 * their existence to someone who may have no right to know. This function
 * is the single place the DELETE flow turns that count into copy, so
 * nothing upstream is tempted to also pass along page titles/ids "just this
 * once"; the sentence itself is shared with the generic
 * SubtreeOperationForbidden path (feedback/blockedSubtreeCopy.ts).
 */
export function describeDeleteOutcome(blockedDescendantCount: number | null | undefined): string | null {
  return describeBlockedSubtree(blockedDescendantCount, 'deleted')
}
