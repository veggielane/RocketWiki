import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { PermissionInspector } from '../PermissionInspector'
import type { EffectivePermissionDetail } from '../effectivePermissionTypes'
import { attr, group } from '../../ruleTypes'

function baseDetail(overrides: Partial<EffectivePermissionDetail> = {}): EffectivePermissionDetail {
  return {
    userId: 'user-1',
    userDisplayName: 'Ada Lovelace',
    spaceRole: 'editor',
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

describe('PermissionInspector', () => {
  it('shows an allowed outcome without a denial reason', () => {
    render(<PermissionInspector detail={baseDetail()} />)
    expect(screen.getByText(/why can ada lovelace.*see this page\?/i)).toBeInTheDocument()
    expect(screen.getByText('View: allowed')).toBeInTheDocument()
    expect(screen.getByText('Edit: allowed')).toBeInTheDocument()
  })

  it('shows the space role', () => {
    render(<PermissionInspector detail={baseDetail({ spaceRole: 'viewer' })} />)
    expect(screen.getByText('Viewer')).toBeInTheDocument()
  })

  it('shows "no role" when no grant matched', () => {
    render(<PermissionInspector detail={baseDetail({ spaceRole: null })} />)
    expect(screen.getByText('No role — no grant matched')).toBeInTheDocument()
  })

  it('explains a denial with the mapped human-readable reason', () => {
    render(
      <PermissionInspector
        detail={baseDetail({ canEdit: false, editDenialReason: 'insufficient-space-role', spaceRole: 'viewer' })}
      />,
    )
    expect(screen.getByText('Edit: denied')).toBeInTheDocument()
    expect(screen.getByText(/below editor/i)).toBeInTheDocument()
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
