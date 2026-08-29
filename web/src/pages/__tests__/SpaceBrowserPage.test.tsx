import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { SpaceBrowserPage } from '../SpaceBrowserPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

const baseSpace = {
  id: 'space-1',
  key: 'ENG',
  name: 'Engineering',
  description: null,
  homepageId: null,
  isReplica: false,
  originInstanceId: 'HIGH',
  viewerIsWatching: false,
  grants: [] as { id: string }[],
}

const tree = [
  {
    id: 'open',
    title: 'Open Page',
    slug: 'open',
    icon: 'ROCKET',
    sortOrder: 0,
    hasRestrictions: false,
    labels: ['onboarding'],
    // design.md §21.5: every page is marked, so every tree node carries one.
    // Two levels here so the tree shows a badge that actually differs.
    marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], prefix: 'UK', label: 'UK OFFICIAL' },
    children: [
      {
        id: 'restricted',
        title: 'Restricted Page',
        slug: 'restricted',
        icon: null,
        sortOrder: 0,
        hasRestrictions: true,
        labels: [],
        // Staged with a caveat the badge deliberately does not show: a list row
        // has no space for one, and §21.1's `levelName` is the level alone.
        marking: {
          level: 'SECRET',
          levelName: 'SECRET',
          eyesOnly: ['UK'],
          prefix: 'UK',
          label: 'UK SECRET [UK EYES ONLY]',
        },
        children: [],
      },
    ],
  },
]

function renderPage({ spaceOverrides = {} as Partial<typeof baseSpace> } = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceTree') return { space: { ...baseSpace, ...spaceOverrides } }
    if (name === 'SpacePageTree') return { pageTree: tree }
    return undefined
  })
  render(
    <MemoryRouter initialEntries={['/spaces/ENG']}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route path="/spaces/:spaceKey" element={<SpaceBrowserPage />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

describe('SpaceBrowserPage', () => {
  it('marks restricted tree nodes with a lock badge (PageTreeNode.hasRestrictions, design.md §6.6)', async () => {
    renderPage()
    expect(await screen.findByText('Restricted Page')).toBeInTheDocument()
    expect(screen.getAllByLabelText('Has access restrictions')).toHaveLength(1)
  })

  it('badges each tree node with its classification (design.md §21)', async () => {
    renderPage()
    expect(await screen.findByText('Restricted Page')).toBeInTheDocument()
    // Every node shows one, because §21.5 leaves no page unmarked — and it is
    // the LEVEL, as text, not colour alone (WCAG 1.4.1).
    const open = screen.getByText('Open Page').closest('a')
    const restricted = screen.getByText('Restricted Page').closest('a')
    expect(open?.textContent).toContain('Classification: OFFICIAL')
    expect(restricted?.textContent).toContain('Classification: SECRET')
    // The badge shows the level, not the marking — the caveat on the
    // restricted node stays on that page's own banners (§21.1's levelName).
    expect(restricted?.textContent).not.toContain('EYES ONLY')
  })

  it("draws each page's own icon, and the generic page glyph for one without", async () => {
    renderPage()
    await screen.findByText('Restricted Page')
    const iconed = screen.getByText('Open Page').closest('a')
    const plain = screen.getByText('Restricted Page').closest('a')
    // MUI's dev-only test id: the glyph is decorative (the title beside it
    // names the page), so it has no accessible name to query by. Every row
    // gets one — a slot filled on only some rows would indent those titles
    // past the rest and read as a hierarchy that isn't there.
    expect(within(iconed as HTMLElement).getByTestId('RocketLaunchOutlinedIcon')).toBeInTheDocument()
    expect(within(plain as HTMLElement).getByTestId('ArticleOutlinedIcon')).toBeInTheDocument()
  })

  it('filters the tree by label into a breadcrumbed result list', async () => {
    renderPage()
    const facet = await screen.findByLabelText('Filter by label')
    fireEvent.mouseDown(facet)
    fireEvent.change(facet, { target: { value: 'onboarding' } })
    fireEvent.click(screen.getByText('onboarding'))

    // The match list replaces the tree: only the labelled page shows.
    expect(await screen.findByRole('region', { name: 'Pages labelled onboarding' })).toBeInTheDocument()
    expect(screen.getByText('Open Page')).toBeInTheDocument()
    expect(screen.queryByText('Restricted Page')).not.toBeInTheDocument()
  })

  it('proactively shows the replica banner from Space.isReplica (design.md §12)', async () => {
    renderPage({ spaceOverrides: { isReplica: true, originInstanceId: 'LOW' } })
    expect(await screen.findByText(/Replica of LOW — read-only/)).toBeInTheDocument()
  })

  it('initializes the watch toggle from Space.viewerIsWatching', async () => {
    renderPage({ spaceOverrides: { viewerIsWatching: true } })
    expect(await screen.findByRole('button', { name: 'Watching' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('hides Grants/Rename/Archive when the grants list comes back empty (not yours to manage)', async () => {
    renderPage()
    await screen.findByRole('heading', { name: 'Engineering' })
    expect(screen.queryByRole('link', { name: 'Grants' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Rename' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Archive' })).not.toBeInTheDocument()
  })

  it('offers space management when the server returns grant rows', async () => {
    // One way in, not four buttons: rename, description, grants, trash and
    // archiving all live on the space's settings page now.
    renderPage({ spaceOverrides: { grants: [{ id: 'g1' }] } })
    const settings = await screen.findByRole('link', { name: 'Space settings' })
    expect(settings).toHaveAttribute('href', '/spaces/ENG/-/admin')
  })

  it('offers no management entry when the server returns no grant rows', async () => {
    // Grants come back only to instance/space admins ("absent, not forbidden"),
    // and a space always has at least one by construction — so an empty list
    // means this caller does not manage it.
    renderPage({ spaceOverrides: { grants: [] } })
    await screen.findByText('Restricted Page')
    expect(screen.queryByRole('link', { name: 'Space settings' })).toBeNull()
  })

  it('has no axe violations with tree, lock badge, replica banner, and management affordances', async () => {
    renderPage({ spaceOverrides: { isReplica: true, originInstanceId: 'LOW', grants: [{ id: 'g1' }] } })
    await screen.findByText('Restricted Page')
    await expectNoAxeViolations()

    // And again with the label filter active — the match list is a separate
    // render path (it once shipped bare <a> children in a <ul>).
    const facet = screen.getByLabelText('Filter by label')
    fireEvent.mouseDown(facet)
    fireEvent.change(facet, { target: { value: 'onboarding' } })
    fireEvent.click(screen.getByText('onboarding'))
    await screen.findByRole('region', { name: 'Pages labelled onboarding' })
    await expectNoAxeViolations()
  })
})
