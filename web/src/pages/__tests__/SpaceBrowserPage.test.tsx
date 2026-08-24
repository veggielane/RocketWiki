import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
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
    sortOrder: 0,
    hasRestrictions: false,
    labels: ['onboarding'],
    children: [
      {
        id: 'restricted',
        title: 'Restricted Page',
        slug: 'restricted',
        sortOrder: 0,
        hasRestrictions: true,
        labels: [],
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
    expect(await screen.findByText(/Mirrored from LOW — read-only/)).toBeInTheDocument()
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
    renderPage({ spaceOverrides: { grants: [{ id: 'g1' }] } })
    expect(await screen.findByRole('link', { name: 'Grants' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Rename' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Archive' })).toBeInTheDocument()
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
