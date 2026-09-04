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
  // The server's own answers (design.md §6.4): whether this caller may
  // manage the space (a role), and whether they may read it (an access grant).
  canManageAccess: false,
  viewerHasAccess: true,
}

/** A page this caller may not read, at its sibling position (design.md §6.7 / §21.8). */
const protectedLeaf = {
  __typename: 'ProtectedTreeNode',
  title: '(protected)',
  sortOrder: 1,
  denial: {
    placeholderTitle: '(protected)',
    noSpaceAccess: false,
    marking: { level: 'TOP_SECRET', levelName: 'TOP SECRET', eyesOnly: ['UK'], ukPrefix: true, selectors: [], label: 'UK TOP SECRET UK EYES ONLY' },
    reasons: [{ gate: 'NATIONAL_CAVEAT', passed: false, countries: ['UK'] }],
  },
}

const tree = [
  {
    __typename: 'PageTreeNode',
    id: 'open',
    title: 'Open Page',
    slug: 'open',
    icon: 'ROCKET',
    sortOrder: 0,
    hasRestrictions: false,
    labels: ['onboarding'],
    // design.md §21.5: every page is marked, so every tree node carries one.
    // Two levels here so the tree shows a badge that actually differs.
    marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], ukPrefix: true, selectors: [], label: 'UK OFFICIAL' },
    children: [
      {
        __typename: 'PageTreeNode',
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
          ukPrefix: true,
          selectors: [],
          label: 'UK SECRET UK EYES ONLY',
        },
        children: [],
      },
      protectedLeaf,
    ],
  },
]

function renderPage({ spaceOverrides = {} as Partial<typeof baseSpace>, pageTree = tree as unknown[] } = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceTree') return { space: { ...baseSpace, ...spaceOverrides } }
    if (name === 'SpacePageTree') return { pageTree }
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
    // By ROLE and accessible name, not `getByLabelText`. The badge used to
    // carry an `aria-label` on an icon MUI had already marked `aria-hidden`
    // (SvgIcon sets it unless `titleAccess` is given), so the label reached
    // nobody — and this assertion passed anyway, because `getByLabelText`
    // matches the attribute without asking whether anything can read it.
    expect(screen.getAllByRole('img', { name: 'This page has access restrictions' })).toHaveLength(1)
  })

  it('badges each readable node with its classification (design.md §21)', async () => {
    renderPage()
    expect(await screen.findByText('Restricted Page')).toBeInTheDocument()
    const open = screen.getByText('Open Page').closest('a')
    const restricted = screen.getByText('Restricted Page').closest('a')
    expect(open?.textContent).toContain('Classification: OFFICIAL')
    expect(restricted?.textContent).toContain('Classification: SECRET')
    // The badge shows the level, not the marking — the caveat on the
    // restricted node stays on that page's own banners (§21.1's levelName).
    expect(restricted?.textContent).not.toContain('EYES ONLY')
  })

  it('shows a page this caller may not read as a protected leaf at its position — its label, not a link', async () => {
    renderPage()
    await screen.findByText('Restricted Page')
    const leaf = screen.getByText('(protected)').closest('li')
    expect(leaf).not.toBeNull()
    // The whole marking, because that is the one fact about the page the
    // caller is allowed to see — and it must not be a link to anywhere.
    expect(within(leaf as HTMLElement).getByText('UK TOP SECRET UK EYES ONLY')).toBeInTheDocument()
    expect(within(leaf as HTMLElement).queryByRole('link')).toBeNull()
    const why = within(leaf as HTMLElement).getByRole('button', { name: 'Why is this page protected?' })
    fireEvent.click(why)
    expect(within(leaf as HTMLElement).getByText('National caveat: Releasable to UK only.')).toBeInTheDocument()
  })

  it("draws each page's own icon, and the generic page glyph for one without", async () => {
    renderPage()
    await screen.findByText('Restricted Page')
    const iconed = screen.getByText('Open Page').closest('a')
    const plain = screen.getByText('Restricted Page').closest('a')
    expect(within(iconed as HTMLElement).getByTestId('RocketLaunchOutlinedIcon')).toBeInTheDocument()
    expect(within(plain as HTMLElement).getByTestId('ArticleOutlinedIcon')).toBeInTheDocument()
  })

  it('filters the tree by label into a breadcrumbed result list, and never lists a placeholder', async () => {
    renderPage()
    const facet = await screen.findByLabelText('Filter by label')
    fireEvent.mouseDown(facet)
    fireEvent.change(facet, { target: { value: 'onboarding' } })
    fireEvent.click(screen.getByText('onboarding'))

    expect(await screen.findByRole('region', { name: 'Pages labelled onboarding' })).toBeInTheDocument()
    expect(screen.getByText('Open Page')).toBeInTheDocument()
    expect(screen.queryByText('Restricted Page')).not.toBeInTheDocument()
    expect(screen.queryByText('(protected)')).not.toBeInTheDocument()
  })

  it('proactively shows the replica banner from Space.isReplica (design.md §12)', async () => {
    renderPage({ spaceOverrides: { isReplica: true, originInstanceId: 'LOW' } })
    expect(await screen.findByText(/Replica of LOW — read-only/)).toBeInTheDocument()
  })

  it('initializes the watch toggle from Space.viewerIsWatching', async () => {
    renderPage({ spaceOverrides: { viewerIsWatching: true } })
    expect(await screen.findByRole('button', { name: 'Watching' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('offers space management on the server\'s canManageAccess, and not otherwise', async () => {
    renderPage({ spaceOverrides: { canManageAccess: true } })
    const settings = await screen.findByRole('link', { name: 'Space settings' })
    expect(settings).toHaveAttribute('href', '/spaces/ENG/-/admin')
  })

  it('offers no management entry to a caller who may not manage the space', async () => {
    renderPage({ spaceOverrides: { canManageAccess: false } })
    await screen.findByText('Restricted Page')
    expect(screen.queryByRole('link', { name: 'Space settings' })).toBeNull()
  })

  it('says why the tree is empty for a caller with no access grant, rather than "no pages yet"', async () => {
    // design.md §6.4: a role grant lists the space; without an access grant
    // the server answers an empty tree, and every page reads as protected.
    renderPage({ spaceOverrides: { canManageAccess: true, viewerHasAccess: false }, pageTree: [] })
    expect(await screen.findByText('You have no access to this space, so its pages are shown as protected.')).toBeInTheDocument()
    expect(screen.queryByText(/No pages yet/)).toBeNull()
  })

  it('declares the same tree dependencies as the sidebar — this page creates pages too', async () => {
    const mock = renderPage()
    await screen.findByText('Restricted Page')

    const treeQuery = mock.operations.find((op) => op.name === 'SpacePageTree')
    expect(treeQuery?.additionalTypenames).toContain('Page')
  })

  it('has no axe violations with tree, lock badge, protected leaf, replica banner, and management affordances', async () => {
    renderPage({ spaceOverrides: { isReplica: true, originInstanceId: 'LOW', canManageAccess: true } })
    await screen.findByText('Restricted Page')
    await expectNoAxeViolations()

    // With the leaf's reasons disclosed.
    fireEvent.click(screen.getByRole('button', { name: 'Why is this page protected?' }))
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
