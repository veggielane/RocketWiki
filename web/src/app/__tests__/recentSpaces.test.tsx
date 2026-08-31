import { beforeEach, describe, expect, it } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { RecentSpaces } from '../RecentSpaces'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'
import { getRecentSpaceKeys, recordSpaceVisit, resetRecentSpacesCache } from '../../spaces/recentSpaces'

/**
 * The rail's shortcut back to where you have been.
 *
 * The load-bearing property is not the list, it is the INTERSECTION: local
 * storage supplies the order, the server-filtered space list supplies
 * membership and everything shown. A key you can no longer view has no match to
 * render from, so it vanishes without anybody writing a check — which is a
 * stronger §6.7 guarantee than one somebody could forget.
 */
const VIEWABLE = [
  { id: 's1', key: 'ENG', name: 'Engineering', description: null, isReplica: false, originInstanceId: null },
  { id: 's2', key: 'OPS', name: 'Operations', description: null, isReplica: false, originInstanceId: null },
  { id: 's3', key: 'LOW', name: 'Low side', description: null, isReplica: true, originInstanceId: 'LOWSIDE' },
]

beforeEach(() => {
  window.localStorage.clear()
  resetRecentSpacesCache()
})

function renderRail({ spaces = VIEWABLE, at = '/search' } = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceList') return { spaces }
    if (name === 'PageSpaceRef') return { page: { id: 'p1', spaceId: 's1', spaceKey: 'ENG' } }
    return undefined
  })
  render(
    <MemoryRouter initialEntries={[at]}>
      <UrqlProvider value={mock.client}>
        <RecentSpaces />
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

describe('what the list shows', () => {
  it('lists recent spaces by name, most recent first', async () => {
    recordSpaceVisit('ENG')
    recordSpaceVisit('OPS')
    renderRail()

    const links = await screen.findAllByRole('link')
    expect(links.map((link) => link.textContent)).toEqual(['Operations', 'Engineering'])
  })

  it('links each one into the space', async () => {
    recordSpaceVisit('OPS')
    renderRail()

    expect(await screen.findByRole('link', { name: 'Operations' })).toHaveAttribute('href', '/spaces/OPS')
  })

  it('shows at most five, however many are remembered', async () => {
    for (const key of ['A', 'B', 'C', 'D', 'E', 'F', 'G']) recordSpaceVisit(key)
    renderRail({
      spaces: ['A', 'B', 'C', 'D', 'E', 'F', 'G'].map((key) => ({
        id: key,
        key,
        name: `Space ${key}`,
        description: null,
        isReplica: false,
        originInstanceId: null,
      })),
    })

    await screen.findByRole('link', { name: 'Space G' })
    expect(screen.getAllByRole('link')).toHaveLength(5)
  })

  it('marks a replica, so a read-only mirror is recognisable before you go there', async () => {
    recordSpaceVisit('LOW')
    renderRail()

    expect(await screen.findByText(/LOWSIDE/)).toBeInTheDocument()
  })
})

describe('a space you cannot see is not on the list', () => {
  it('drops a remembered key that is not in the viewable space list', async () => {
    // §6.7: access removed, or the space deleted — either way it must be
    // indistinguishable from never having existed. Nothing "hides" it; there is
    // simply nothing to render it from.
    recordSpaceVisit('SECRETPROJECT')
    recordSpaceVisit('ENG')
    renderRail()

    await screen.findByRole('link', { name: 'Engineering' })
    expect(screen.queryByText(/SECRETPROJECT/)).not.toBeInTheDocument()
    expect(screen.getAllByRole('link')).toHaveLength(1)
  })

  it('renders nothing at all when no remembered space survives the intersection', async () => {
    recordSpaceVisit('SECRETPROJECT')
    const { container } = render(
      <MemoryRouter initialEntries={['/search']}>
        <UrqlProvider value={createMockUrqlClient((name) => (name === 'SpaceList' ? { spaces: [] } : undefined)).client}>
          <RecentSpaces />
        </UrqlProvider>
      </MemoryRouter>,
    )

    await waitFor(() => expect(container).toBeEmptyDOMElement())
  })

  it('never leaks the key itself while the space list is still loading', async () => {
    // The order comes from storage, which is instant; membership comes from a
    // query, which is not. Rendering from storage alone would put an
    // unauthorised key on screen for exactly one frame.
    renderRail({ spaces: [] })
    recordSpaceVisit('SECRETPROJECT')

    expect(screen.queryByText(/SECRETPROJECT/)).not.toBeInTheDocument()
  })
})

describe('the space you are in is not a shortcut to itself', () => {
  it('excludes the current space', async () => {
    // The picker below shows it as its selected value and the tree shows its
    // pages; a third copy in one narrow rail is noise.
    recordSpaceVisit('ENG')
    recordSpaceVisit('OPS')
    renderRail({ at: '/spaces/OPS/-/browse' })

    expect(await screen.findByRole('link', { name: 'Engineering' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Operations' })).not.toBeInTheDocument()
  })

  it('excludes it whatever casing the URL used', async () => {
    recordSpaceVisit('ENG')
    recordSpaceVisit('OPS')
    renderRail({ at: '/spaces/ops/-/browse' })

    await screen.findByRole('link', { name: 'Engineering' })
    expect(screen.queryByRole('link', { name: 'Operations' })).not.toBeInTheDocument()
  })

  it('records the space of a page route, which names no space in its URL', async () => {
    recordSpaceVisit('OPS')
    renderRail({ at: '/pages/p1' })

    // ENG came from the page, so it is now current and excluded — and the fact
    // that OPS is all that remains is the proof it was recorded at all.
    await waitFor(() => expect(screen.queryByRole('link', { name: 'Engineering' })).not.toBeInTheDocument())
    expect(screen.getByRole('link', { name: 'Operations' })).toBeInTheDocument()
  })
})

describe('the empty and accessible cases', () => {
  it('renders nothing for an account that has been nowhere', () => {
    // No "Recent" heading over an empty list: a promise the rail cannot keep.
    const { container } = render(
      <MemoryRouter initialEntries={['/search']}>
        <UrqlProvider value={createMockUrqlClient((name) => (name === 'SpaceList' ? { spaces: VIEWABLE } : undefined)).client}>
          <RecentSpaces />
        </UrqlProvider>
      </MemoryRouter>,
    )

    expect(container).toBeEmptyDOMElement()
  })

  it('gives the section a named landmark rather than a loose list', async () => {
    recordSpaceVisit('ENG')
    renderRail()

    expect(await screen.findByRole('navigation', { name: 'Recent' })).toBeInTheDocument()
  })

  it('has no axe violations', async () => {
    recordSpaceVisit('ENG')
    recordSpaceVisit('LOW')
    renderRail()
    await screen.findByRole('link', { name: 'Engineering' })
    await expectNoAxeViolations()
  })
})

describe('being somewhere is what puts it on the list', () => {
  it('records the space of the route it is rendered on', async () => {
    // The list is useless if nothing ever writes to it, and every other
    // assertion here seeds the store by hand — so without this one, removing
    // the recording entirely leaves the suite green.
    renderRail({ at: '/spaces/OPS/-/browse' })

    await waitFor(() => expect(getRecentSpaceKeys()).toContain('OPS'))
  })

  it('records the space of a page route, whose URL names no space', async () => {
    // `/pages/{id}` resolves its space through the page, so being on a page
    // counts as being in its space.
    renderRail({ at: '/pages/p1' })

    await waitFor(() => expect(getRecentSpaceKeys()).toContain('ENG'))
  })

  it('records the server’s canonical key, not the casing in the URL', async () => {
    // Otherwise /spaces/ops and /spaces/OPS would each take a slot, and the
    // rail would show the same space twice under two spellings.
    renderRail({ at: '/spaces/ops/-/browse' })

    await waitFor(() => expect(getRecentSpaceKeys()).toEqual(['OPS']))
  })
})
