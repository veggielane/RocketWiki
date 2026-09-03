import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { CreateSpacePage } from '../CreateSpacePage'
import { expectNoAxeViolations } from '../../test/axe'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { serializeRuleNode } from '../../access/ruleSerializer'
import { group, user } from '../../access/ruleTypes'

function renderPage({ meId = 'sub-1' as string | null } = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'CurrentUser')
      return meId === null
        ? undefined
        : {
            me: {
              id: meId, email: null, name: 'Chris', groups: [], isAuthenticated: true,
              isInstanceAdmin: true, localUserId: 'user-1', hasAvatar: false,
              clearance: 'SECRET', nationality: ['UK'], selectorEligibility: ['FRUIT'],
            },
          }
    if (name === 'RuleVocabulary') return { groups: ['engineering'], attributeRegistry: [] }
    if (name === 'SelectorCategories')
      return { selectorCategories: [{ name: 'FRUIT', description: null, requiresAttribute: true, values: ['APPLE', 'BANANA'] }] }
    if (name === 'CreateSpace') return { createSpace: { space: { id: 'space-new', key: 'ENG', name: 'Engineering' }, error: null } }
    return undefined
  })
  render(
    <MemoryRouter>
      <UrqlProvider value={mock.client}>
        <CreateSpacePage />
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

function fillKeyAndName() {
  fireEvent.change(screen.getByLabelText('Key'), { target: { value: 'ENG' } })
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Engineering' } })
}

function sentGrants(mock: ReturnType<typeof renderPage>) {
  const op = mock.operations.find((o) => o.name === 'CreateSpace')
  if (!op) throw new Error('CreateSpace was not sent')
  return (op.variables as { initialGrants: Record<string, unknown>[] }).initialGrants
}

/**
 * design.md §6.5.1 / §6.4: a space is born administrable (a Space admin
 * role grant, pre-filled with the creator) and visible to nobody (access is
 * a separate, optional grant). These tests make "cannot be skipped, cannot
 * default to everyone" a property of the code rather than a comment.
 */
describe('CreateSpacePage — administrator required, access optional', () => {
  it('pre-fills the administrator grant with the creator, the one subject the form can be sure of', async () => {
    renderPage()
    expect(await screen.findByLabelText('User ID')).toHaveValue('sub-1')
    expect(screen.getByRole('heading', { name: 'Who administers this space' })).toBeInTheDocument()
  })

  it('stays disabled until key and name are present, and never offers a skip', async () => {
    renderPage()
    await screen.findByLabelText('User ID')
    const createButton = screen.getByRole('button', { name: 'Create space' })
    expect(createButton).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Key'), { target: { value: 'ENG' } })
    expect(createButton).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Engineering' } })
    expect(createButton).toBeEnabled()
    expect(screen.queryByRole('button', { name: /skip/i })).not.toBeInTheDocument()
  })

  it('sends only the Space admin role grant when no access grant is added — nobody can see the space yet', async () => {
    const mock = renderPage()
    await screen.findByLabelText('User ID')
    fillKeyAndName()
    fireEvent.click(screen.getByRole('button', { name: 'Create space' }))
    await waitFor(() => expect(mock.operations.some((op) => op.name === 'CreateSpace')).toBe(true))
    expect(sentGrants(mock)).toEqual([
      { kind: 'ROLE_GRANT', role: 'SPACE_ADMIN', expressionJson: serializeRuleNode(user('sub-1')) },
    ])
    // Never `everyone`, and never an access grant nobody asked for.
    expect(JSON.stringify(sentGrants(mock))).not.toContain('everyone')
  })

  it('sends an access grant with its selector values when one is added', async () => {
    const mock = renderPage()
    await screen.findByLabelText('User ID')
    fillKeyAndName()
    fireEvent.click(screen.getByRole('button', { name: 'Add access grant' }))
    // The access builder starts on a blank group condition — no default.
    const groupField = await screen.findByLabelText('Group')
    fireEvent.change(groupField, { target: { value: 'engineering' } })
    const picker = screen.getByLabelText('Selector values')
    fireEvent.mouseDown(picker)
    fireEvent.change(picker, { target: { value: 'APP' } })
    fireEvent.click(await screen.findByRole('option', { name: 'APPLE' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Create space' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: 'Create space' }))
    await waitFor(() => expect(mock.operations.some((op) => op.name === 'CreateSpace')).toBe(true))
    expect(sentGrants(mock)).toEqual([
      { kind: 'ROLE_GRANT', role: 'SPACE_ADMIN', expressionJson: serializeRuleNode(user('sub-1')) },
      {
        kind: 'ACCESS_GRANT',
        expressionJson: serializeRuleNode(group('engineering')),
        selectorValues: [{ category: 'FRUIT', value: 'APPLE' }],
      },
    ])
  })

  it('will not submit an added access grant that is incomplete', async () => {
    renderPage()
    await screen.findByLabelText('User ID')
    fillKeyAndName()
    fireEvent.click(screen.getByRole('button', { name: 'Add access grant' }))
    await screen.findByLabelText('Group')
    expect(screen.getByRole('button', { name: 'Create space' })).toBeDisabled()
    // Removing the half-made grant makes the form submittable again.
    fireEvent.click(screen.getByRole('button', { name: 'Remove access grant' }))
    expect(screen.getByRole('button', { name: 'Create space' })).toBeEnabled()
  })

  it('explains that access is separate and has no default like everyone', () => {
    renderPage()
    expect(screen.getByText(/no default like "everyone"/i)).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Who can see it' })).toBeInTheDocument()
  })

  it('has no axe violations (WCAG 2.2 AA policy, test/axe.ts)', async () => {
    renderPage()
    await screen.findByLabelText('User ID')
    fireEvent.click(screen.getByRole('button', { name: 'Add access grant' }))
    await screen.findByLabelText('Group')
    await expectNoAxeViolations()
  })
})
