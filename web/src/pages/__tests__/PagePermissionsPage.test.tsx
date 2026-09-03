import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { PagePermissionsPage } from '../PagePermissionsPage'
import { createMockUrqlClient, type MockClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

type Restriction = {
  ruleId: string
  pageId: string
  pageTitle: string
  inherited: boolean
  action: 'VIEW' | 'EDIT'
  expressionJson: string
  createdAtUtc: string
  updatedAtUtc: string
  updatedByDisplayName: string | null
}

const ownRestriction: Restriction = {
  ruleId: 'rule-own',
  pageId: 'page-1',
  pageTitle: 'Export Runbook',
  inherited: false,
  action: 'VIEW',
  expressionJson: '{"group":"export-cleared"}',
  createdAtUtc: '2026-08-01T00:00:00Z',
  updatedAtUtc: '2026-08-01T00:00:00Z',
  updatedByDisplayName: 'Ada Lovelace',
}

const inheritedRestriction: Restriction = {
  ruleId: 'rule-inh',
  pageId: 'page-0',
  pageTitle: 'Restricted Parent',
  inherited: true,
  action: 'VIEW',
  expressionJson: '{"group":"engineering"}',
  createdAtUtc: '2026-07-01T00:00:00Z',
  updatedAtUtc: '2026-07-01T00:00:00Z',
  updatedByDisplayName: null,
}

const selfDetail = {
  userId: 'sub-1',
  userDisplayName: 'Ada Lovelace',
  hasSpaceAccess: true,
  spaceRole: 'SPACE_ADMIN',
  isReplicaSpace: false,
  canView: true,
  canEdit: true,
  viewDenialReason: null,
  editDenialReason: null,
  viewGates: [],
  editGates: [],
  viewRestrictions: [],
  editRestrictions: [],
}

function renderPage({
  canManageAccess = true,
  restrictions = [] as Restriction[],
  isInstanceAdmin = false,
} = {}): MockClient {
  const mock = createMockUrqlClient((name) => {
    if (name === 'PagePermissions')
      return { page: { id: 'page-1', title: 'Export Runbook', spaceKey: 'ENG', canManageAccess, restrictions } }
    if (name === 'RuleVocabulary') return { groups: ['engineering', 'export-cleared'], attributeRegistry: [] }
    if (name === 'CurrentUser')
      return {
        me: {
          id: 'sub-1',
          email: null,
          name: 'Ada',
          groups: [],
          isAuthenticated: true,
          isInstanceAdmin,
          localUserId: 'user-1',
        },
      }
    if (name === 'EffectivePermission') return { effectivePermission: selfDetail }
    if (name === 'CreateAccessRule')
      return { createAccessRule: { rule: { id: 'rule-new', kind: 'PAGE_RESTRICTION', role: null, expressionJson: '{}' }, error: null } }
    if (name === 'UpdateAccessRule')
      return { updateAccessRule: { rule: { id: 'rule-own', kind: 'PAGE_RESTRICTION', role: null, expressionJson: '{}' }, error: null } }
    if (name === 'DeleteAccessRule') return { deleteAccessRule: { deletedRuleId: 'rule-own', error: null } }
    return undefined
  })
  render(
    <MemoryRouter initialEntries={['/pages/page-1/permissions']}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route path="/pages/:pageId/permissions" element={<PagePermissionsPage />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

describe('PagePermissionsPage', () => {
  it('renders as not-found when the server says canManageAccess is false — absent, not forbidden', async () => {
    renderPage({ canManageAccess: false })
    expect(await screen.findByText(/doesn't exist, or you don't have access/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save restrictions' })).not.toBeInTheDocument()
  })

  it('has no axe violations with own + inherited restrictions listed', async () => {
    renderPage({ restrictions: [inheritedRestriction, ownRestriction] })
    await screen.findByText('Inherited from Restricted Parent')
    await expectNoAxeViolations()
  })

  it('lists inherited restrictions flagged with their source page, read-only with an edit-at-source link', async () => {
    renderPage({ restrictions: [inheritedRestriction, ownRestriction] })

    expect(await screen.findByText('Inherited from Restricted Parent')).toBeInTheDocument()
    expect(screen.getByText('group: engineering')).toBeInTheDocument()
    // Inherited rules are edited at their source page, not here.
    expect(screen.getByRole('link', { name: 'Restricted Parent' })).toHaveAttribute(
      'href',
      '/pages/page-0/permissions',
    )
  })

  it("loads the page's own restriction into an editable rule-builder row", async () => {
    renderPage({ restrictions: [ownRestriction] })

    expect(await screen.findByDisplayValue('export-cleared')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Save restrictions' })).toBeInTheDocument()
  })

  it('creates a PAGE_RESTRICTION rule via createAccessRule on save', async () => {
    const mock = renderPage({ restrictions: [] })

    fireEvent.click(await screen.findByRole('button', { name: 'Add restriction' }))
    fireEvent.change(screen.getByLabelText('Group'), { target: { value: 'export-cleared' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save restrictions' }))

    await waitFor(() => {
      const creates = mock.operations.filter((op) => op.name === 'CreateAccessRule')
      expect(creates).toHaveLength(1)
      expect(creates[0]!.variables['input']).toEqual({
        kind: 'PAGE_RESTRICTION',
        pageId: 'page-1',
        action: 'VIEW',
        expressionJson: '{"group":"export-cleared"}',
      })
    })
  })

  it('deletes a removed restriction via deleteAccessRule on save', async () => {
    const mock = renderPage({ restrictions: [ownRestriction] })

    fireEvent.click(await screen.findByRole('button', { name: 'Remove restriction' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save restrictions' }))

    await waitFor(() => {
      const deletes = mock.operations.filter((op) => op.name === 'DeleteAccessRule')
      expect(deletes).toHaveLength(1)
      expect(deletes[0]!.variables['input']).toEqual({ accessRuleId: 'rule-own' })
    })
  })

  it('mounts the permission inspector in self mode', async () => {
    renderPage({})
    expect(await screen.findByText('View: allowed')).toBeInTheDocument()
    // Non-instance-admins get no what-if form — the server would refuse a
    // foreign subject from them anyway.
    expect(screen.queryByText('Inspect another principal (what-if)')).not.toBeInTheDocument()
  })

  it('offers the what-if subject form to instance admins only', async () => {
    renderPage({ isInstanceAdmin: true })
    expect(await screen.findByText('Inspect another principal (what-if)')).toBeInTheDocument()
    expect(screen.getByLabelText('User ID (token subject)')).toBeInTheDocument()
  })
})
