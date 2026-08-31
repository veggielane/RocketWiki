import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { SpaceSettingsPage } from '../SpaceSettingsPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

const space = {
  id: 'space-1',
  key: 'ENG',
  name: 'Engineering',
  description: 'Engineering space',
  homepageId: null as string | null,
  isReplica: false,
  originInstanceId: 'HIGH',
  viewerIsWatching: false,
  grants: [{ id: 'g1' }] as { id: string }[],
  // The server's own permission answer. `grants` no longer decides anything
  // on this screen — see the zero-grant test below for why that mattered.
  canManageAccess: true,
  owner: { id: "u1", displayName: "Ada Lovelace", hasAvatar: false } as
    | { id: string; displayName: string; hasAvatar: boolean }
    | null,
}

const node = (id: string, title: string, children: unknown[] = []) => ({
  id,
  title,
  slug: title.toLowerCase().replace(/ /g, '-'),
  icon: null,
  sortOrder: 0,
  hasRestrictions: false,
  labels: [],
  marking: { level: 'OFFICIAL', levelName: 'OFFICIAL' },
  children,
})

const tree = [node('page-1', 'Handbook', [node('page-2', 'Onboarding')])]

function renderSettings({ overrides = {} as Partial<typeof space> } = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceTree') return { space: { ...space, ...overrides } }
    if (name === 'SpacePageTree') return { pageTree: tree }
    if (name === 'RenameSpace') return { renameSpace: { space: { ...space, ...overrides }, error: null } }
    if (name === 'SetSpaceHomepage')
      return { setSpaceHomepage: { space: { id: 'space-1', homepageId: 'page-2' }, error: null } }
    return undefined
  })
  render(
    <MemoryRouter initialEntries={['/spaces/ENG/-/admin']}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route path="/spaces/:spaceKey/-/admin" element={<SpaceSettingsPage />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

async function chooseDefaultPage(name: string) {
  fireEvent.mouseDown(await screen.findByLabelText('Default page'))
  fireEvent.click(await screen.findByRole('option', { name }))
}

describe('SpaceSettingsPage default page', () => {
  it('offers every page in the space, and None', async () => {
    renderSettings()
    fireEvent.mouseDown(await screen.findByLabelText('Default page'))
    expect(await screen.findByRole('option', { name: 'None' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Handbook' })).toBeInTheDocument()
    // Nested pages are choosable too — a default page need not be a root.
    expect(screen.getByRole('option', { name: 'Onboarding' })).toBeInTheDocument()
    // "(top level)" is a place to CREATE a page, not a page — the space is the
    // thing being defaulted, so the only "nothing" here is None.
    expect(screen.queryByRole('option', { name: '(top level)' })).toBeNull()
  })

  it('sends the chosen page on save', async () => {
    const mock = renderSettings()
    await chooseDefaultPage('Onboarding')
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      const call = mock.operations.find((op) => op.name === 'SetSpaceHomepage')
      expect(call?.variables['input']).toEqual({ spaceId: 'space-1', pageId: 'page-2' })
    })
  })

  it('sends null to clear it, rather than omitting the field', async () => {
    // Clearing has to be expressible or the setting is a one-way door; the
    // server reads null as "no default page" and omission would not reach it.
    const mock = renderSettings({ overrides: { homepageId: 'page-1' } })
    await chooseDefaultPage('None')
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      const call = mock.operations.find((op) => op.name === 'SetSpaceHomepage')
      expect(call?.variables['input']).toEqual({ spaceId: 'space-1', pageId: null })
    })
  })

  it('does not write the homepage when only the name changed', async () => {
    // One Save issues two writes, and the second only when it has something to
    // say — otherwise every rename would rewrite the homepage and audit it.
    const mock = renderSettings()
    await screen.findByLabelText('Default page')
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Engineering renamed' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(mock.operations.some((op) => op.name === 'RenameSpace')).toBe(true))
    expect(mock.operations.some((op) => op.name === 'SetSpaceHomepage')).toBe(false)
  })

  it('offers nothing to change when the caller does not manage the space', async () => {
    // MUI marks a disabled Select with aria-disabled on the combobox rather than
    // the `disabled` attribute (the real <input> is hidden), so this is the
    // assertion that reflects what a user is actually told.
    renderSettings({ overrides: { canManageAccess: false } })
    expect(await screen.findByLabelText('Default page')).toHaveAttribute('aria-disabled', 'true')
  })

  it('lets a permitted manager manage a space that has NO grants', async () => {
    // The bug the proxy had. `canManage` was `grants.length > 0`, which
    // conflated "you may SEE the grants" with "there ARE grants to see" — the
    // resolver hands a permitted manager the real, empty list, and the screen
    // read that as "not permitted". An instance admin on a grantless space was
    // shown the read-only notice with every control disabled, while the server
    // would have accepted all of them. An imported replica is exactly the space
    // that can be both grantless and ownerless, so this also unblocked the one
    // person who could assign its owner.
    renderSettings({ overrides: { grants: [], canManageAccess: true } })
    expect(await screen.findByLabelText('Default page')).not.toHaveAttribute('aria-disabled', 'true')
    expect(screen.queryByText(/managing it needs instance admin/)).not.toBeInTheDocument()
  })

  it('has no axe violations with the picker present', async () => {
    renderSettings()
    await screen.findByLabelText('Default page')
    await expectNoAxeViolations()
  })
})

describe('SpaceSettingsPage owner section', () => {
  it('carries the owner section, beside the other space-level controls', async () => {
    // Every other owner test renders the section directly, so without this one
    // the section could be deleted from the page and the suite would stay green.
    renderSettings()
    expect(await screen.findByRole('heading', { level: 2, name: 'Owner' })).toBeInTheDocument()
    expect(screen.getByText('Ada Lovelace')).toBeInTheDocument()
  })

  it('shows the ownerless state a replica arrives in', async () => {
    // The case that motivated the feature: ownership does not sync, so an
    // imported space starts without one and somebody has to take it on.
    renderSettings({ overrides: { owner: null, isReplica: true } })
    expect(await screen.findByText(/No owner assigned/)).toBeInTheDocument()
    // NOT disabled the way rename and archive are on a replica — this is the one
    // space write exempt from the read-only rule.
    expect(screen.getByRole('button', { name: 'Assign an owner' })).toBeEnabled()
  })
})
