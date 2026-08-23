import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { PermissionInspectorPanel } from '../PermissionInspectorPanel'
import { createMockUrqlClient, type MockClient } from '../../../test/mockUrqlClient'

const wireDetail = {
  userId: 'sub-1',
  userDisplayName: 'Ada Lovelace',
  spaceRole: 'SPACE_ADMIN',
  isReplicaSpace: false,
  canView: true,
  canEdit: false,
  viewDenialReason: null,
  editDenialReason: 'insufficient-space-role',
  viewRestrictions: [
    {
      ruleId: 'r1',
      pageId: 'p0',
      pageTitle: 'Restricted Parent',
      action: 'VIEW',
      expressionJson: '{"group":"export-cleared"}',
      passed: true,
    },
  ],
  editRestrictions: [],
}

function renderPanel({
  isInstanceAdmin = false,
  allowSubjectInput = false,
  detail = wireDetail as typeof wireDetail | null,
} = {}): MockClient {
  const mock = createMockUrqlClient((name) => {
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
    if (name === 'EffectivePermission') return { effectivePermission: detail }
    return undefined
  })
  render(
    <UrqlProvider value={mock.client}>
      <PermissionInspectorPanel pageId="page-1" allowSubjectInput={allowSubjectInput} />
    </UrqlProvider>,
  )
  return mock
}

describe('PermissionInspectorPanel', () => {
  it('renders the wire shape through the enum/expression mapping — SPACE_ADMIN role, parsed restriction expression', async () => {
    renderPanel()
    expect(await screen.findByText('Space admin')).toBeInTheDocument()
    expect(screen.getByText('View: allowed')).toBeInTheDocument()
    expect(screen.getByText('Edit: denied')).toBeInTheDocument()
    // The server's denial-reason vocabulary is byte-exact — mapped copy shows.
    expect(screen.getByText(/below editor/i)).toBeInTheDocument()
    // expressionJson parsed client-side into the shared rule summary.
    expect(screen.getByText('group: export-cleared')).toBeInTheDocument()
    expect(screen.getByText('Restricted Parent')).toBeInTheDocument()
  })

  it('shows an absent-voiced message when the query returns null (not viewable / refused)', async () => {
    renderPanel({ detail: null })
    expect(await screen.findByText(/couldn't inspect permissions/i)).toBeInTheDocument()
  })

  it('sends the admin-entered principal as the subject variable on Inspect', async () => {
    const mock = renderPanel({ isInstanceAdmin: true, allowSubjectInput: true })

    fireEvent.change(await screen.findByLabelText('User ID (token subject)'), { target: { value: 'sub-other' } })
    fireEvent.click(screen.getByRole('button', { name: 'Inspect' }))

    await waitFor(() => {
      const inspections = mock.operations.filter((op) => op.name === 'EffectivePermission')
      const last = inspections.at(-1)!
      expect(last.variables['subject']).toEqual({ userId: 'sub-other', groups: [], attributes: [] })
    })
    expect(await screen.findByText(/what-if result/i)).toBeInTheDocument()
  })

  it('never offers the subject form when allowSubjectInput is off, even for admins', async () => {
    renderPanel({ isInstanceAdmin: true, allowSubjectInput: false })
    await screen.findByText('Space admin')
    expect(screen.queryByText('Inspect another principal (what-if)')).not.toBeInTheDocument()
  })
})
