import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { SpaceGrantsPage } from '../SpaceGrantsPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

const accessGrant = {
  id: 'g-access',
  kind: 'ACCESS_GRANT',
  role: null,
  action: null,
  expressionJson: '{"everyone":true}',
  selectorValues: [{ category: 'FRUIT', value: 'APPLE' }],
}

const roleGrant = {
  id: 'g-role',
  kind: 'ROLE_GRANT',
  role: 'EDITOR',
  action: null,
  expressionJson: '{"group":"engineering"}',
  selectorValues: [],
}

function renderPage({ canManageAccess = true, grants = [accessGrant, roleGrant] } = {}) {
  const mock = createMockUrqlClient((name, op) => {
    if (name === 'SpaceGrants')
      return { space: { id: 'space-1', key: 'ENG', name: 'Engineering', canManageAccess, viewerHasAccess: true, grants } }
    if (name === 'RuleVocabulary') return { groups: ['engineering', 'ops'], attributeRegistry: [] }
    if (name === 'SelectorCategories')
      return {
        selectorCategories: [
          { name: 'FRUIT', description: null, values: ['APPLE', 'BANANA'] },
          { name: 'REGION', description: null, values: ['NORTH', 'SOUTH'] },
        ],
      }
    if (name === 'CreateAccessRule') {
      const input = (op.variables as { input: Record<string, unknown> }).input
      return { createAccessRule: { rule: { id: 'g-new', ...input, selectorValues: input['selectorValues'] ?? [] }, error: null } }
    }
    if (name === 'UpdateAccessRule') {
      const input = (op.variables as { input: Record<string, unknown> }).input
      return { updateAccessRule: { rule: { ...accessGrant, ...input }, error: null } }
    }
    if (name === 'DeleteAccessRule')
      return { deleteAccessRule: { deletedRuleId: (op.variables as { input: { accessRuleId: string } }).input.accessRuleId, error: null } }
    return undefined
  })
  render(
    <MemoryRouter initialEntries={['/spaces/ENG/-/grants']}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route path="/spaces/:spaceKey/-/grants" element={<SpaceGrantsPage />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

const accessSection = () => screen.findByRole('region', { name: 'Access' })
const rolesSection = () => screen.findByRole('region', { name: 'Roles' })

const sent = (mock: ReturnType<typeof renderPage>, name: string) =>
  mock.operations.filter((op) => op.name === name).map((op) => (op.variables as { input: unknown }).input)

/**
 * design.md §6.4: two kinds of grant, two sections, each saved as a diff
 * against what was loaded. Access grants carry selector values (several per
 * category allowed, §21.15); role grants carry Editor or Space admin and no
 * visibility. There is no viewer role anywhere on this screen.
 */
describe('SpaceGrantsPage', () => {
  it('gates on the server\'s canManageAccess, not on the grant list', async () => {
    renderPage({ canManageAccess: false })
    expect(await screen.findByText("This page doesn't exist, or you don't have access to it.")).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Grants' })).toBeNull()
  })

  it('shows an Access section and a Roles section, and says which decides what', async () => {
    renderPage()
    expect(await accessSection()).toBeInTheDocument()
    expect(await rolesSection()).toBeInTheDocument()
    expect(screen.getByText(/Access grants decide who may see this space's pages/)).toBeInTheDocument()
    expect(screen.queryByText(/viewer/i)).toBeNull()
  })

  it('sorts the loaded grants by kind — the access grant with its values, the role grant with its role', async () => {
    renderPage()
    const access = await accessSection()
    expect(within(access).getByText('FRUIT: APPLE')).toBeInTheDocument()
    expect(within(access).queryByRole('combobox', { name: 'Grant role' })).toBeNull()
    const roles = await rolesSection()
    expect(within(roles).getByRole('combobox', { name: 'Grant role' })).toHaveTextContent('Editor')
    expect(within(roles).queryByLabelText('Selector values')).toBeNull()
  })

  it('offers no save while nothing changed', async () => {
    renderPage()
    const access = await accessSection()
    expect(within(access).getByRole('button', { name: 'Save access grants' })).toBeDisabled()
    const roles = await rolesSection()
    expect(within(roles).getByRole('button', { name: 'Save role grants' })).toBeDisabled()
  })

  it('lets an access grant carry several values in one category, and sends them on update', async () => {
    const mock = renderPage()
    const access = await accessSection()
    const picker = within(access).getByLabelText('Selector values')
    fireEvent.mouseDown(picker)
    fireEvent.change(picker, { target: { value: 'BAN' } })
    fireEvent.click(await screen.findByRole('option', { name: 'BANANA' }))
    expect(within(access).getByText('FRUIT: BANANA')).toBeInTheDocument()
    fireEvent.click(within(access).getByRole('button', { name: 'Save access grants' }))
    await waitFor(() => expect(sent(mock, 'UpdateAccessRule')).toHaveLength(1))
    expect(sent(mock, 'UpdateAccessRule')[0]).toEqual({
      accessRuleId: 'g-access',
      expressionJson: '{"everyone":true}',
      selectorValues: [
        { category: 'FRUIT', value: 'APPLE' },
        { category: 'FRUIT', value: 'BANANA' },
      ],
    })
    // The role section was not touched and sends nothing.
    expect(sent(mock, 'CreateAccessRule')).toHaveLength(0)
  })

  it('creates a new role grant with its role and no selector values', async () => {
    const mock = renderPage()
    const roles = await rolesSection()
    fireEvent.click(within(roles).getByRole('button', { name: 'Add role grant' }))
    // The loaded role grant already has a Group condition; the new row's is
    // the last one rendered.
    await waitFor(() => expect(within(roles).getAllByLabelText('Group')).toHaveLength(2))
    fireEvent.change(within(roles).getAllByLabelText('Group').at(-1)!, { target: { value: 'ops' } })
    // The new row defaults to Editor; make it Space admin.
    const selects = within(roles).getAllByRole('combobox', { name: 'Grant role' })
    fireEvent.mouseDown(selects[1]!)
    fireEvent.click(await screen.findByRole('option', { name: 'Space admin' }))
    await waitFor(() => expect(within(roles).getByRole('button', { name: 'Save role grants' })).toBeEnabled())
    fireEvent.click(within(roles).getByRole('button', { name: 'Save role grants' }))
    await waitFor(() => expect(sent(mock, 'CreateAccessRule')).toHaveLength(1))
    expect(sent(mock, 'CreateAccessRule')[0]).toEqual({
      kind: 'ROLE_GRANT',
      spaceId: 'space-1',
      role: 'SPACE_ADMIN',
      expressionJson: '{"group":"ops"}',
    })
  })

  it('creates a new access grant with its selector values', async () => {
    const mock = renderPage({ grants: [roleGrant] })
    const access = await accessSection()
    expect(within(access).getByText(/nobody can see this space's pages yet/)).toBeInTheDocument()
    fireEvent.click(within(access).getByRole('button', { name: 'Add access grant' }))
    fireEvent.change(await within(access).findByLabelText('Group'), { target: { value: 'engineering' } })
    const picker = within(access).getByLabelText('Selector values')
    fireEvent.mouseDown(picker)
    fireEvent.change(picker, { target: { value: 'NOR' } })
    fireEvent.click(await screen.findByRole('option', { name: 'NORTH' }))
    await waitFor(() => expect(within(access).getByRole('button', { name: 'Save access grants' })).toBeEnabled())
    fireEvent.click(within(access).getByRole('button', { name: 'Save access grants' }))
    await waitFor(() => expect(sent(mock, 'CreateAccessRule')).toHaveLength(1))
    expect(sent(mock, 'CreateAccessRule')[0]).toEqual({
      kind: 'ACCESS_GRANT',
      spaceId: 'space-1',
      expressionJson: '{"group":"engineering"}',
      selectorValues: [{ category: 'REGION', value: 'NORTH' }],
    })
  })

  it('deletes a removed grant on save', async () => {
    const mock = renderPage()
    const roles = await rolesSection()
    fireEvent.click(within(roles).getByRole('button', { name: 'Remove grant' }))
    await waitFor(() => expect(within(roles).getByRole('button', { name: 'Save role grants' })).toBeEnabled())
    fireEvent.click(within(roles).getByRole('button', { name: 'Save role grants' }))
    await waitFor(() => expect(sent(mock, 'DeleteAccessRule')).toEqual([{ accessRuleId: 'g-role' }]))
  })

  it('has no axe violations with both sections populated', async () => {
    renderPage()
    await accessSection()
    await rolesSection()
    await expectNoAxeViolations()
  })
})
