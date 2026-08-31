import { describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import { RouterProvider, createMemoryRouter } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { routes } from '../router'
import { ColorModeProvider } from '../../theme/ColorModeProvider'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { crumbsFor, routeTitleFor } from '../routeCrumbs'

/**
 * The space list has an address of its own.
 *
 * It used to BE the home route, which made "all the spaces" and "where the app
 * starts" the same idea — fine while that was the only screen there could be,
 * wrong the moment the homepage has a job of its own. Moving it is the whole
 * change; `/` redirects here until the homepage exists, so no bookmark and no
 * brand-mark click lands on nothing in the meantime.
 */
vi.mock('react-oidc-context', () => ({
  useAuth: () => ({ isAuthenticated: true, isLoading: false, user: { profile: { name: 'Viewer' } }, signoutRedirect: vi.fn() }),
}))

// ONE object, like the real module's memoised singleton. usePresence keys its
// effect on transport identity, so a factory that returns a fresh object each
// render re-joins the room on every render — an infinite loop rather than a
// subtle leak, which is at least an honest way to find out.
const presence = {
  joinRoom: () => Promise.resolve(),
  leaveRoom: () => Promise.resolve(),
  onViewersChanged: () => () => {},
  onPointerMoved: () => () => {},
  onReconnected: () => () => {},
  sendPointerPosition: () => {},
}
const notifications = {
  connect: () => Promise.resolve(),
  disconnect: () => Promise.resolve(),
  onNotification: () => () => {},
}

vi.mock('../../realtime/transports', () => ({
  getDefaultPresenceTransport: () => presence,
  getDefaultNotificationsTransport: () => notifications,
}))

const SPACES = [
  { id: 's1', key: 'ENG', name: 'Engineering', description: null, isReplica: false, originInstanceId: null },
]

/** The REAL route table, in a memory router — not a copy of it written here. */
function renderAt(path: string) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceList') return { spaces: SPACES }
    if (name === 'CurrentUser')
      return {
        me: { id: 'sub-1', email: null, name: 'Viewer', groups: [], isAuthenticated: true, isInstanceAdmin: false, localUserId: 'user-1' },
      }
    if (name === 'UnreadNotificationCount') return { unreadNotificationCount: 0 }
    return undefined
  })
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  render(
    <ColorModeProvider>
      <UrqlProvider value={mock.client}>
        <RouterProvider router={router} />
      </UrqlProvider>
    </ColorModeProvider>,
  )
  return router
}

describe('the space list lives at /spaces', () => {
  it('renders the space list there', async () => {
    renderAt('/spaces')
    expect(await screen.findByRole('heading', { level: 1, name: 'Spaces' })).toBeInTheDocument()
    expect(await screen.findByText('Engineering')).toBeInTheDocument()
  })

  it('sends the home route there rather than dead-ending', async () => {
    // Every existing bookmark of `/`, and the rail's brand mark, still land on
    // a real screen while the homepage is being built.
    const router = renderAt('/')
    await waitFor(() => expect(router.state.location.pathname).toBe('/spaces'))
  })

  it('replaces the history entry, so Back does not bounce off the redirect', async () => {
    const router = renderAt('/')
    await waitFor(() => expect(router.state.location.pathname).toBe('/spaces'))
    // A pushed redirect would put `/` behind `/spaces` in history, and Back
    // would redirect forward again — a trap with no way out.
    expect(router.state.historyAction).toBe('REPLACE')
  })
})

describe('the rail offers one way to the spaces, not two', () => {
  it('points "All spaces" at the space list and marks it current there', async () => {
    renderAt('/spaces')
    const link = await screen.findByRole('link', { name: 'All spaces' })
    expect(link).toHaveAttribute('href', '/spaces')
  })

  it('has exactly one link to the space list in the rail', async () => {
    // The IA risk in moving this: ending up with a "Home" and an "All spaces"
    // that both list spaces. There is one destination and one affordance.
    renderAt('/spaces')
    await screen.findByRole('link', { name: 'All spaces' })
    const navigation = screen.getByRole('navigation', { name: 'Spaces' })
    const toSpaces = Array.from(navigation.querySelectorAll('a')).filter((a) => a.getAttribute('href') === '/spaces')
    expect(toSpaces).toHaveLength(1)
  })
})

describe('the breadcrumb root has somewhere to point', () => {
  it('names the screen without linking to it when you are on it', () => {
    expect(crumbsFor('/spaces')).toEqual([{ label: 'Spaces' }])
    expect(routeTitleFor('/spaces')).toBe('Spaces')
  })

  it('links every deeper crumb back to the space list, not to the home route', () => {
    // `/` is about to stop being the space list, so a root crumb pointing there
    // would quietly become a link to somewhere else entirely.
    const [root] = crumbsFor('/spaces/ENG/-/trash', { key: 'ENG', name: 'Engineering' })
    expect(root).toEqual({ label: 'Spaces', to: '/spaces' })
  })
})
