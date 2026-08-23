import { describe, expect, it } from 'vitest'
import { mapPageAction, mapSpaceRole, toEffectivePermissionDetail } from '../mapEffectivePermission'
import { serializeRuleNode } from '../../ruleSerializer'
import { attr, group } from '../../ruleTypes'
import type { EffectivePermissionQuery } from '../../../graphql/generated/graphql'

type Wire = NonNullable<EffectivePermissionQuery['effectivePermission']>

function wire(overrides: Partial<Wire> = {}): Wire {
  return {
    userId: 'sub-1',
    userDisplayName: 'Ada Lovelace',
    spaceRole: 'EDITOR',
    isReplicaSpace: false,
    canView: true,
    canEdit: true,
    viewDenialReason: null,
    editDenialReason: null,
    viewRestrictions: [],
    editRestrictions: [],
    ...overrides,
  }
}

describe('mapSpaceRole — the GraphQL enum casing to the web casing', () => {
  it.each([
    ['VIEWER', 'viewer'],
    ['EDITOR', 'editor'],
    ['SPACE_ADMIN', 'spaceAdmin'],
  ] as const)('maps %s to %s', (api, web) => {
    expect(mapSpaceRole(api)).toBe(web)
  })

  it('keeps null (no grant matched) as null', () => {
    expect(mapSpaceRole(null)).toBeNull()
  })
})

describe('mapPageAction', () => {
  it('maps VIEW/EDIT to view/edit', () => {
    expect(mapPageAction('VIEW')).toBe('view')
    expect(mapPageAction('EDIT')).toBe('edit')
  })
})

describe('toEffectivePermissionDetail', () => {
  it('carries scalars through and re-cases the role', () => {
    const detail = toEffectivePermissionDetail(
      wire({ spaceRole: 'SPACE_ADMIN', canEdit: false, editDenialReason: 'insufficient-space-role' }),
    )
    expect(detail.userDisplayName).toBe('Ada Lovelace')
    expect(detail.spaceRole).toBe('spaceAdmin')
    expect(detail.canEdit).toBe(false)
    expect(detail.editDenialReason).toBe('insufficient-space-role')
  })

  it('parses each restriction check\'s expressionJson into a RuleNode', () => {
    const expression = attr('nationality', ['NZ', 'US'])
    const detail = toEffectivePermissionDetail(
      wire({
        viewRestrictions: [
          {
            ruleId: 'r1',
            pageId: 'p1',
            pageTitle: 'Export Docs',
            action: 'VIEW',
            expressionJson: serializeRuleNode(expression),
            passed: false,
          },
        ],
      }),
    )
    expect(detail.viewRestrictions).toEqual([
      { ruleId: 'r1', pageId: 'p1', pageTitle: 'Export Docs', action: 'view', expression, passed: false },
    ])
  })

  it('maps edit restrictions with the EDIT action', () => {
    const detail = toEffectivePermissionDetail(
      wire({
        editRestrictions: [
          {
            ruleId: 'r2',
            pageId: 'p1',
            pageTitle: 'Export Docs',
            action: 'EDIT',
            expressionJson: serializeRuleNode(group('engineering')),
            passed: true,
          },
        ],
      }),
    )
    expect(detail.editRestrictions[0]!.action).toBe('edit')
    expect(detail.editRestrictions[0]!.passed).toBe(true)
  })

  it('keeps a malformed expression as null instead of throwing or dropping the check', () => {
    const detail = toEffectivePermissionDetail(
      wire({
        viewRestrictions: [
          { ruleId: 'r-bad', pageId: 'p1', pageTitle: 'Broken', action: 'VIEW', expressionJson: '{oops', passed: false },
        ],
      }),
    )
    expect(detail.viewRestrictions).toHaveLength(1)
    expect(detail.viewRestrictions[0]!.expression).toBeNull()
  })
})
