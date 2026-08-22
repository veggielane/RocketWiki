import type { RuleNode } from '../ruleTypes'

/** Mirrors `RocketWiki.Core.Enums.SpaceRole`. */
export type SpaceRole = 'viewer' | 'editor' | 'spaceAdmin'

/** Mirrors `RocketWiki.Core.Enums.PageAction`. */
export type PageAction = 'view' | 'edit'

/**
 * One restriction evaluated against the inspected user, for one action, on
 * one page in the page-plus-ancestors chain (design.md §6.4: restrictions
 * accumulate down the tree).
 *
 * This is *richer* than what `PermissionCheckResult` (see
 * `RocketWiki.Core/Access/PermissionCheckResult.cs`) currently returns.
 * `EffectivePermissionCalculator.CheckRestrictions` evaluates restrictions
 * in order and returns on the *first* failure — correct and cheap for the
 * real canView/canEdit gate, but useless for an inspector that needs to
 * show every restriction's pass/fail. A real `effectivePermissionDetail`
 * resolver would need a non-short-circuiting sibling to that method (same
 * `RuleEvaluator` call per rule, just not returning early) purely for this
 * diagnostic view. Flagging that here rather than have the frontend type
 * silently imply a backend capability that doesn't exist yet.
 */
export interface RestrictionCheck {
  ruleId: string
  /** Which page in the page-plus-ancestors chain this restriction is attached to. */
  pageId: string
  pageTitle: string
  action: PageAction
  expression: RuleNode
  passed: boolean
}

/**
 * Extends `EffectivePermission` (`RocketWiki.Core/Access/EffectivePermission.cs`)
 * with the space-role value and the full restriction breakdown design.md
 * §6.6 asks the inspector to show — `EffectivePermission` itself only
 * carries a single collapsed denial reason string per action.
 */
export interface EffectivePermissionDetail {
  userId: string
  userDisplayName: string
  /** Null means no space grant matched — fails closed, no default role (design.md §6.4). */
  spaceRole: SpaceRole | null
  isReplicaSpace: boolean
  canView: boolean
  canEdit: boolean
  viewDenialReason: string | null
  editDenialReason: string | null
  /** Every view restriction on the page and its ancestors, evaluated (not short-circuited). */
  viewRestrictions: RestrictionCheck[]
  /** Every edit restriction on the page and its ancestors, evaluated (not short-circuited). */
  editRestrictions: RestrictionCheck[]
}
