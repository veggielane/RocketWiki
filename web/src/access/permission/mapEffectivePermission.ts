import type {
  EffectivePermissionQuery,
  PageAction as ApiPageAction,
  SpaceRole as ApiSpaceRole,
} from '../../graphql/generated/graphql'
import { tryParseRuleNode } from '../ruleSerializer'
import type { EffectivePermissionDetail, PageAction, RestrictionCheck, SpaceRole } from './effectivePermissionTypes'

/**
 * Adapts the wire shape of `effectivePermission` (design.md §6.6's shipped
 * inspector) to the inspector's view model: GraphQL enum casing
 * (`SPACE_ADMIN` → `spaceAdmin`, `VIEW` → `view`) and `expressionJson`
 * parsed with the same ruleSerializer the builder writes with. Pure and
 * separately tested so the casing contract can't silently drift inside a
 * component.
 */

type EffectivePermissionView = NonNullable<EffectivePermissionQuery['effectivePermission']>
type RestrictionCheckView = EffectivePermissionView['viewRestrictions'][number]

export function mapSpaceRole(role: ApiSpaceRole | null): SpaceRole | null {
  switch (role) {
    case 'VIEWER':
      return 'viewer'
    case 'EDITOR':
      return 'editor'
    case 'SPACE_ADMIN':
      return 'spaceAdmin'
    case null:
      return null
  }
}

export function mapPageAction(action: ApiPageAction): PageAction {
  return action === 'VIEW' ? 'view' : 'edit'
}

function mapRestrictionCheck(check: RestrictionCheckView): RestrictionCheck {
  return {
    ruleId: check.ruleId,
    pageId: check.pageId,
    pageTitle: check.pageTitle,
    action: mapPageAction(check.action),
    // A malformed stored expression must not crash the inspector — the rule
    // still denies (fails closed, design.md §6.3); it renders as unreadable.
    expression: tryParseRuleNode(check.expressionJson).node,
    passed: check.passed,
  }
}

export function toEffectivePermissionDetail(view: EffectivePermissionView): EffectivePermissionDetail {
  return {
    userId: view.userId,
    userDisplayName: view.userDisplayName,
    spaceRole: mapSpaceRole(view.spaceRole),
    isReplicaSpace: view.isReplicaSpace,
    canView: view.canView,
    canEdit: view.canEdit,
    viewDenialReason: view.viewDenialReason,
    editDenialReason: view.editDenialReason,
    viewRestrictions: view.viewRestrictions.map(mapRestrictionCheck),
    editRestrictions: view.editRestrictions.map(mapRestrictionCheck),
  }
}
