import type { RuleNode } from '../ruleTypes'
import type { GateCheck } from '../denial/describeAccessGate'

/**
 * Web-side casing of the GraphQL `SpaceRole` enum (`EDITOR|SPACE_ADMIN`) —
 * mapped in mapEffectivePermission.ts. There is no viewer: design.md §6.4
 * separates SEEING (an access grant) from DOING (a role grant), and a role
 * is only ever one of the two that let you act.
 */
export type SpaceRole = 'editor' | 'spaceAdmin'

/** Web-side casing of the GraphQL `PageAction` enum (`VIEW|EDIT`). */
export type PageAction = 'view' | 'edit'

/**
 * One restriction evaluated against the inspected user, for one action, on
 * one page in the page-plus-ancestors chain (design.md §6.4: restrictions
 * accumulate down the tree). Mirrors the API's `RestrictionCheckView`,
 * with `expressionJson` parsed client-side into a `RuleNode` — the wire
 * carries JSON, the same contract the rule builder writes.
 *
 * The full per-restriction pass/fail breakdown exists because the server
 * has a non-short-circuiting explain path
 * (`EffectivePermissionCalculator.Explain`, §6.6) — the enforcement gate
 * still stops at the first failure; the inspector deliberately doesn't.
 */
export interface RestrictionCheck {
  ruleId: string
  /** Which page in the page-plus-ancestors chain this restriction is attached to. */
  pageId: string
  pageTitle: string
  action: PageAction
  /** Null when the stored expression couldn't be parsed — the rule still denies (fails closed, §6.3) and must still be shown. */
  expression: RuleNode | null
  passed: boolean
}

/**
 * The inspector's view model — the API's `EffectivePermissionDetail` with
 * enums re-cased and expressions parsed (mapEffectivePermission.ts).
 * design.md §6.6: the space-access and space-role computations, every gate
 * on the view and edit ladders with pass/fail, and every restriction with
 * pass/fail — not just the first failing one.
 */
export interface EffectivePermissionDetail {
  userId: string
  userDisplayName: string
  /** Whether an ACCESS grant matched — the gate that lets this user see anything here at all (design.md §6.4). */
  hasSpaceAccess: boolean
  /** The highest ROLE grant that matched, or null — no role is the ordinary state for a reader, and confers no visibility either way. */
  spaceRole: SpaceRole | null
  isReplicaSpace: boolean
  canView: boolean
  canEdit: boolean
  viewDenialReason: string | null
  editDenialReason: string | null
  /** The whole view ladder — space access, classification, selector eligibility and grant, national caveat, restriction — each evaluated. */
  viewGates: GateCheck[]
  /** The edit ladder: the view gates again, then replica and role. */
  editGates: GateCheck[]
  /** Every view restriction on the page and its ancestors, evaluated (not short-circuited). */
  viewRestrictions: RestrictionCheck[]
  /** Every edit restriction on the page and its ancestors, evaluated (not short-circuited). */
  editRestrictions: RestrictionCheck[]
}
