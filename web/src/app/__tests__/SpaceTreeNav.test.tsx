import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
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

const node = (id: string, title: string, children: unknown[] = []) => ({
  id,
  title,
  hasRestrictions: false,
  labels: [],
  marking: { level: 'OFFICIAL', levelName: 'Official' },
  children,
})

const TREE = {
  pageTree: [
    node('page-1', 'Launch notes', [node('page-2', 'Static fire')]),
    node('page-3', 'Runbooks'),
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

  it('has no axe violations with a nested tree', async () => {
    // The tree nests <li> inside <li>; a list item outside a list element is a
    // violation, and it is exactly what a first pass at this component shipped.
    renderNav('/spaces/ENG')
    await screen.findByRole('link', { name: 'Static fire' })
    await expectNoAxeViolations()
  })
})
