import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider } from 'urql'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { SpaceTreeNav } from '../SpaceTreeNav'
import { expectNoAxeViolations } from '../../test/axe'

const SPACES = {
  spaces: [
    { id: 'space-1', key: 'ENG', name: 'Engineering', description: null, isReplica: false, originInstanceId: null },
    { id: 'space-2', key: 'OPS', name: 'Operations', description: null, isReplica: false, originInstanceId: null },
  ],
}

const node = (
  id: string,
  title: string,
  slug: string,
  children: unknown[] = [],
  icon: string | null = null,
) => ({
  id,
  title,
  slug,
  icon,
  hasRestrictions: false,
  labels: [],
  marking: { level: 'OFFICIAL', levelName: 'Official' },
  children,
})

const TREE = {
  pageTree: [
    node('page-1', 'Launch notes', 'launch-notes', [
      node('page-2', 'Static fire', 'static-fire', [], 'SATELLITE'),
    ], 'ROCKET'),
    node('page-3', 'Runbooks', 'runbooks'),
  ],
}

function renderNav(path: string, overrides: Record<string, unknown> = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceList') return SPACES
    if (name === 'SpacePageTree') return TREE
    if (name === 'PageSpaceRef') return { page: { id: 'page-2', spaceId: 'space-1', spaceKey: 'ENG' } }
    return undefined
  })
  render(
    <Provider value={mock.client}>
      <MemoryRouter initialEntries={[path]}>
        <SpaceTreeNav {...overrides} />
      </MemoryRouter>
    </Provider>,
  )
  return mock
}

describe('SpaceTreeNav', () => {
  it('lists spaces without a page tree when not inside one', async () => {
    renderNav('/')
    expect(await screen.findByRole('link', { name: 'Engineering' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Operations' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Launch notes' })).toBeNull()
  })

  it('expands the page tree of the space you are browsing', async () => {
    renderNav('/spaces/ENG')
    expect(await screen.findByRole('link', { name: 'Launch notes' })).toBeInTheDocument()
    // Nested children too — the hierarchy, not a flat list of roots.
    expect(screen.getByRole('link', { name: 'Static fire' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Runbooks' })).toBeInTheDocument()
  })

  it('expands the tree from a PAGE route, resolving the space from the page id', async () => {
    // A page URL carries no space, so the nav has to look it up — otherwise the
    // tree would collapse the moment you clicked into a page from it.
    const mock = renderNav('/pages/page-2')
    expect(await screen.findByRole('link', { name: 'Launch notes' })).toBeInTheDocument()
    expect(mock.operations.some((op) => op.name === 'PageSpaceRef')).toBe(true)
  })

  it('does not resolve a page reference when the route already names the space', async () => {
    const mock = renderNav('/spaces/ENG')
    await screen.findByRole('link', { name: 'Launch notes' })
    expect(mock.operations.some((op) => op.name === 'PageSpaceRef')).toBe(false)
  })

  it('only expands the active space, not every space', async () => {
    const mock = renderNav('/spaces/ENG')
    await screen.findByRole('link', { name: 'Launch notes' })
    const treeQueries = mock.operations.filter((op) => op.name === 'SpacePageTree')
    expect(treeQueries.every((op) => op.variables['spaceId'] === 'space-1')).toBe(true)
  })

  it('says so rather than showing nothing when the space has no pages', async () => {
    const mock = createMockUrqlClient((name) => {
      if (name === 'SpaceList') return SPACES
      if (name === 'SpacePageTree') return { pageTree: [] }
      return undefined
    })
    render(
      <Provider value={mock.client}>
        <MemoryRouter initialEntries={['/spaces/ENG']}>
          <SpaceTreeNav />
        </MemoryRouter>
      </Provider>,
    )
    expect(await screen.findByText('No pages yet')).toBeInTheDocument()
  })

  it("draws each page's own icon, and the generic page glyph for one without", async () => {
    renderNav('/spaces/ENG')
    const iconed = await screen.findByRole('link', { name: 'Launch notes' })
    const plain = screen.getByRole('link', { name: 'Runbooks' })
    // MUI's own dev-only test id: the glyph is deliberately decorative (the
    // title beside it names the page), so there is no accessible name to
    // query it by — and adding one purely for this assertion would make a
    // screen reader read every row twice.
    expect(within(iconed).getByTestId('RocketLaunchOutlinedIcon')).toBeInTheDocument()
    expect(within(plain).getByTestId('ArticleOutlinedIcon')).toBeInTheDocument()
  })

  it('falls back to the generic glyph for an icon this build has never heard of', async () => {
    // design.md §12: a page can arrive from an instance whose icon set is
    // ahead of this build's. It renders as an ordinary page, not a hole.
    renderNav('/spaces/ENG')
    const unknown = await screen.findByRole('link', { name: 'Static fire' })
    expect(within(unknown).getByTestId('ArticleOutlinedIcon')).toBeInTheDocument()
  })

  it('links every page by its readable address, at any depth', async () => {
    renderNav('/spaces/ENG')
    expect(await screen.findByRole('link', { name: 'Launch notes' }))
      .toHaveAttribute('href', '/spaces/ENG/launch-notes')
    // The hierarchy is absent from the URL on purpose, so a child's address is
    // no longer than its parent's — that is what lets a page be moved.
    expect(screen.getByRole('link', { name: 'Static fire' }))
      .toHaveAttribute('href', '/spaces/ENG/static-fire')
  })

  it('marks the page you are on when you arrived by its slug', async () => {
    // The ordinary route now. Matching on slug rather than id is what keeps
    // this working without a second query to turn the slug into an id.
    renderNav('/spaces/ENG/static-fire')
    const active = await screen.findByRole('link', { name: 'Static fire' })
    expect(active).toHaveClass('Mui-selected')
    expect(screen.getByRole('link', { name: 'Runbooks' })).not.toHaveClass('Mui-selected')
  })

  it('still marks it when you arrived by id, which is what a search result links to', async () => {
    renderNav('/pages/page-2')
    const active = await screen.findByRole('link', { name: 'Static fire' })
    expect(active).toHaveClass('Mui-selected')
  })

  it('marks nothing in the tree on a space system page', async () => {
    // /spaces/ENG/-/admin is not a page, and `-` can never be a slug, so the
    // slug arm must not go looking for a node to highlight.
    renderNav('/spaces/ENG/-/admin')
    const runbooks = await screen.findByRole('link', { name: 'Runbooks' })
    expect(runbooks).not.toHaveClass('Mui-selected')
    expect(screen.getByRole('link', { name: 'Static fire' })).not.toHaveClass('Mui-selected')
  })

  it('has no axe violations with a nested tree', async () => {
    // The tree nests <li> inside <li>; a list item outside a list element is a
    // violation, and it is exactly what a first pass at this component shipped.
    renderNav('/spaces/ENG')
    await screen.findByRole('link', { name: 'Static fire' })
    await expectNoAxeViolations()
  })
})
