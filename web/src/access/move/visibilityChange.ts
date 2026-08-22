import type { RuleNode } from '../ruleTypes'

/** One restriction rule attached somewhere in a page's ancestor chain. */
export interface RestrictionSummary {
  ruleId: string
  pageId: string
  pageTitle: string
  action: 'view' | 'edit'
  expression: RuleNode
}

export interface MoveTargetOption {
  id: string | null // null = move to the space root (no parent)
  title: string
  /** All view/edit restrictions inherited from this target's own ancestor chain (design.md §6.4). */
  ancestorRestrictions: RestrictionSummary[]
}

export interface VisibilityChange {
  changed: boolean
  /** Restrictions that would newly apply after the move. */
  added: RestrictionSummary[]
  /** Restrictions that currently apply but would no longer, after the move. */
  removed: RestrictionSummary[]
}

/**
 * design.md §6.4: "Moving a page re-evaluates nothing — restrictions are
 * positional, so a page moved under a restricted parent immediately
 * inherits that parent's restrictions. The move UI warns when a move would
 * change who can see a page."
 *
 * The page's *own* restrictions never change in a move — only the set
 * inherited from ancestors does, since restrictions accumulate down the
 * tree. So the comparison is exactly the ancestor-restriction set at the
 * current position vs. at the prospective one; the page's own rules cancel
 * out and don't need to be part of the comparison.
 *
 * This can't tell you *which users* gain or lose access (that needs
 * evaluating every restriction's rule expression against every user, which
 * belongs on the server) — it tells an admin *what rules* would start or
 * stop applying, which is what design.md asks the UI to warn about.
 */
export function computeVisibilityChange(
  currentAncestorRestrictions: RestrictionSummary[],
  prospectiveAncestorRestrictions: RestrictionSummary[],
): VisibilityChange {
  const added = prospectiveAncestorRestrictions.filter(
    (r) => !currentAncestorRestrictions.some((c) => c.ruleId === r.ruleId),
  )
  const removed = currentAncestorRestrictions.filter(
    (c) => !prospectiveAncestorRestrictions.some((r) => r.ruleId === c.ruleId),
  )
  return { changed: added.length > 0 || removed.length > 0, added, removed }
}
