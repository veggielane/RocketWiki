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
    renderSettings({ overrides: { grants: [] } })
    expect(await screen.findByLabelText('Default page')).toHaveAttribute('aria-disabled', 'true')
  })

  it('has no axe violations with the picker present', async () => {
    renderSettings()
    await screen.findByLabelText('Default page')
    await expectNoAxeViolations()
  })
})
