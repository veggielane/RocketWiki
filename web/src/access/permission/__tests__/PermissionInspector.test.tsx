import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import { PermissionInspector } from '../PermissionInspector'
import type { EffectivePermissionDetail } from '../effectivePermissionTypes'
import { attr, group } from '../../ruleTypes'

function baseDetail(overrides: Partial<EffectivePermissionDetail> = {}): EffectivePermissionDetail {
  return {
    userId: 'user-1',
    userDisplayName: 'Ada Lovelace',
    hasSpaceAccess: true,
    spaceRole: 'editor',
    isReplicaSpace: false,
    canView: true,
    canEdit: true,
    viewDenialReason: null,
    editDenialReason: null,
    viewGates: [],
    editGates: [],
    viewRestrictions: [],
    editRestrictions: [],
    ...overrides,
  }
}

describe('PermissionInspector', () => {
  it('shows an allowed outcome without a denial reason', () => {
    render(<PermissionInspector detail={baseDetail()} />)
    expect(screen.getByText(/why can ada lovelace.*see this page\?/i)).toBeInTheDocument()
    expect(screen.getByText('View: allowed')).toBeInTheDocument()
    expect(screen.getByText('Edit: allowed')).toBeInTheDocument()
  })

  it('shows space access and space role as two separate facts (design.md §6.4)', () => {
    render(<PermissionInspector detail={baseDetail({ hasSpaceAccess: true, spaceRole: 'spaceAdmin' })} />)
    expect(screen.getByText('Space access')).toBeInTheDocument()
    expect(screen.getByText('Granted')).toBeInTheDocument()
    expect(screen.getByText('Space admin')).toBeInTheDocument()
  })

  it('shows "no access grant matched" and "None" for a role — a role-only manager, or a reader with no role', () => {
    render(<PermissionInspector detail={baseDetail({ hasSpaceAccess: false, spaceRole: null })} />)
    expect(screen.getByText('No access grant matched')).toBeInTheDocument()
    expect(screen.getByText('None')).toBeInTheDocument()
    expect(screen.queryByText(/viewer/i)).toBeNull()
  })

  it('explains a denial with the mapped human-readable reason', () => {
    render(
      <PermissionInspector
        detail={baseDetail({ canEdit: false, editDenialReason: 'insufficient-space-role', spaceRole: null })}
      />,
    )
    expect(screen.getByText('Edit: denied')).toBeInTheDocument()
    expect(screen.getByText(/Editor or Space admin/)).toBeInTheDocument()
  })

  it('lists every view gate and edit gate with its pass/fail state and sentence', () => {
    render(
      <PermissionInspector
        detail={baseDetail({
          canView: false,
          viewDenialReason: 'selector:not_granted:FRUIT',
          viewGates: [
            { gate: 'SPACE_ACCESS', passed: true },
            { gate: 'MARKING_UNAVAILABLE', passed: true },
            { gate: 'SELECTOR_GRANT', passed: false, category: 'FRUIT', value: 'BANANA' },
            { gate: 'NATIONAL_CAVEAT', passed: true },
          ],
          editGates: [{ gate: 'ROLE', passed: false, requiredRole: 'EDITOR' }],
        })}
      />,
    )
    const viewGates = screen.getByRole('list', { name: 'View gates' })
    expect(within(viewGates).getAllByRole('listitem')).toHaveLength(4)
    expect(within(viewGates).getByText('You hold an access grant in this space.')).toBeInTheDocument()
    expect(within(viewGates).getByText("This page's marking is present.")).toBeInTheDocument()
    expect(within(viewGates).getByText('BANANA is not granted to you in this space.')).toBeInTheDocument()
    expect(within(viewGates).getAllByRole('img', { name: 'Passed' })).toHaveLength(3)
    expect(within(viewGates).getAllByRole('img', { name: 'Failed' })).toHaveLength(1)
    const editGates = screen.getByRole('list', { name: 'Edit gates' })
    expect(within(editGates).getByText('Needs the Editor role in this space.')).toBeInTheDocument()
  })

  it('shows the replica banner when the space is a read-only replica', () => {
    render(<PermissionInspector detail={baseDetail({ isReplicaSpace: true, canEdit: false, editDenialReason: 'replica-read-only' })} />)
    expect(screen.getByText(/replica of another instance/i)).toBeInTheDocument()
  })

  it('lists each restriction with its pass/fail state and rule expression', () => {
    render(
      <PermissionInspector
        detail={baseDetail({
          canView: false,
          viewDenialReason: 'restriction:page-1:rule-1',
          viewRestrictions: [
            {
              ruleId: 'rule-1',
              pageId: 'page-1',
              pageTitle: 'Export-Controlled Docs',
              action: 'view',
              expression: group('export-cleared'),
              passed: false,
            },
            {
              ruleId: 'rule-2',
              pageId: 'page-0',
              pageTitle: 'Parent Space Home',
              action: 'view',
              expression: attr('nationality', ['NZ', 'US']),
              passed: true,
            },
          ],
        })}
      />,
    )

    expect(screen.getByText('Export-Controlled Docs')).toBeInTheDocument()
    expect(screen.getByText('(failed)')).toBeInTheDocument()
    expect(screen.getByText('Parent Space Home')).toBeInTheDocument()
    expect(screen.getByText('(passed)')).toBeInTheDocument()
    expect(screen.getByText('group: export-cleared')).toBeInTheDocument()
  })

  it('shows a placeholder when a restriction list is empty', () => {
    render(<PermissionInspector detail={baseDetail()} />)
    expect(screen.getAllByText('None on this page or its ancestors.')).toHaveLength(2)
  })

  it('renders a restriction whose stored expression could not be parsed as explicitly unreadable, never dropped', () => {
    render(
      <PermissionInspector
        detail={baseDetail({
          canView: false,
          viewDenialReason: 'restriction:page-1:rule-1',
          viewRestrictions: [
            { ruleId: 'rule-1', pageId: 'page-1', pageTitle: 'Broken Rule Page', action: 'view', expression: null, passed: false },
          ],
        })}
      />,
    )
    expect(screen.getByText('Broken Rule Page')).toBeInTheDocument()
    expect(screen.getByText(/couldn't be read/i)).toBeInTheDocument()
  })
})
