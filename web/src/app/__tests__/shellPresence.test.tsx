import { beforeEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { RouterProvider, createMemoryRouter, Link } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { AppShell } from '../AppShell'
import { ColorModeProvider } from '../../theme/ColorModeProvider'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { FakePresenceTransport } from '../../realtime/FakePresenceTransport'
import { useSetPresenceRoom } from '../../presence/PresenceRoomContext'

/**
 * Presence now belongs to the shell, and this is why that matters.
 *
 * It used to be mounted inside two page components, so seventeen other screens
 * had no presence at all. Moving it up means the shell joins and leaves a room
 * on every navigation — which is the part that has to be right: a room left
 * behind after the reader has moved on keeps listing them as present on a
 * screen they are not looking at, and that is the "leaked subscription is a
 * live data leak" case, not a resource leak.
 */
// The rail reads the signed-in user for its account menu; nothing here needs a
// real OIDC client, and constructing one would put a network call in a
// presence test.
vi.mock('react-oidc-context', () => ({
  useAuth: () => ({ user: { profile: { name: 'Viewer' } }, isAuthenticated: true, signoutRedirect: vi.fn() }),
}))

const holder = vi.hoisted(() => ({ transport: undefined as unknown }))
vi.mock('../../realtime/transports', () => ({
  getDefaultPresenceTransport: () => holder.transport,
  // The notification bell lives in the shell's header and opens its own
  // connection; a fake with no traffic keeps it out of this test's way.
  getDefaultNotificationsTransport: () => ({
    connect: () => Promise.resolve(),
    disconnect: () => Promise.resolve(),
    onNotification: () => () => {},
  }),
}))

let transport: FakePresenceTransport

beforeEach(() => {
  transport = new FakePresenceTransport()
  holder.transport = transport
})

/** A screen that claims a room of its own, the way every page screen does. */
function PageScreen({ pageId }: { pageId: string }) {
  useSetPresenceRoom(`page:${pageId}`)
  return (
    <>
      <div>page screen</div>
      <Nav />
    </>
  )
}

function renderShell(initialEntry = '/search') {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceList') return { spaces: [] }
    if (name === 'CurrentUser')
      return {
        me: { id: 'sub-1', email: null, name: 'Viewer', groups: [], isAuthenticated: true, isInstanceAdmin: false, localUserId: 'user-1' },
      }
    if (name === 'UnreadNotificationCount') return { unreadNotificationCount: 0 }
    return undefined
  })
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppShell />,
        children: [
          { path: 'search', element: <Nav /> },
          { path: 'ask', element: <Nav /> },
          { path: 'pages/:pageId', element: <PageScreen pageId="p1" /> },
          { path: 'spaces/:spaceKey/:slug', element: <PageScreen pageId="p1" /> },
        ],
      },
    ],
    { initialEntries: [initialEntry] },
  )
  render(
    <ColorModeProvider>
      <UrqlProvider value={mock.client}>
        <RouterProvider router={router} />
      </UrqlProvider>
    </ColorModeProvider>,
  )
  return router
}

/** In-router links, so a navigation is a real one rather than a remount. */
function Nav() {
  return (
    <>
      <div>screen</div>
      <Link to="/ask">go to ask</Link>
      <Link to="/pages/p1">go to page by id</Link>
      <Link to="/spaces/ENG/runbook">go to page by slug</Link>
    </>
  )
}

describe('the shell joins a room for the screen you are on', () => {
  it('joins the route’s room on a screen that is not a page', async () => {
    renderShell('/search')
    await waitFor(() => expect(transport.currentlyJoinedRooms).toEqual(['site:/search']))
  })

  it('leaves the old room and joins the new one on navigation', async () => {
    renderShell('/search')
    await waitFor(() => expect(transport.currentlyJoinedRooms).toEqual(['site:/search']))

    fireEvent.click(screen.getByRole('link', { name: 'go to ask' }))

    await waitFor(() => expect(transport.currentlyJoinedRooms).toEqual(['site:/ask']))
    // Explicitly left, not merely superseded: the server keys groups on the
    // connection, which does not change when the route does.
    expect(transport.leftRooms).toContain('site:/search')
  })

  it('never leaves a screen’s room still joined behind it', async () => {
    renderShell('/search')
    await waitFor(() => expect(transport.currentlyJoinedRooms).toHaveLength(1))

    fireEvent.click(screen.getByRole('link', { name: 'go to ask' }))

    // One room at a time, always — this is the assertion that fails if leaving
    // is ever dropped, and rooms start accumulating per screen visited.
    await waitFor(() => expect(transport.currentlyJoinedRooms).toHaveLength(1))
  })
})

describe('a page’s two addresses are one room', () => {
  it('joins page:{id} from the id route', async () => {
    renderShell('/pages/p1')
    await waitFor(() => expect(transport.currentlyJoinedRooms).toEqual(['page:p1']))
  })

  it('joins the same room from the readable address', async () => {
    // The whole reason page screens name their own room: this URL carries a
    // slug, and a path-derived room would put a reader here and an editor at
    // /pages/p1/edit in two different rooms.
    renderShell('/spaces/ENG/runbook')
    await waitFor(() => expect(transport.currentlyJoinedRooms).toEqual(['page:p1']))
  })

  it('joins no room at all while a page screen has not named one', async () => {
    // `presenceRoomFor` returns null for page routes, so nothing is joined
    // until the screen resolves its id — never a throwaway path-shaped room.
    renderShell('/pages/p1')
    await waitFor(() => expect(transport.joinedRooms).toEqual(['page:p1']))
    expect(transport.joinedRooms.some((room) => room.startsWith('site:/pages'))) .toBe(false)
  })

  it('drops a page room when leaving for a non-page screen', async () => {
    renderShell('/pages/p1')
    await waitFor(() => expect(transport.currentlyJoinedRooms).toEqual(['page:p1']))

    fireEvent.click(screen.getByRole('link', { name: 'go to ask' }))

    await waitFor(() => expect(transport.currentlyJoinedRooms).toEqual(['site:/ask']))
  })
})

describe('reconnect rejoins the room being looked at now', () => {
  it('rejoins the current room, not the one that was open when the drop began', async () => {
    renderShell('/search')
    await waitFor(() => expect(transport.currentlyJoinedRooms).toEqual(['site:/search']))
    fireEvent.click(screen.getByRole('link', { name: 'go to ask' }))
    await waitFor(() => expect(transport.currentlyJoinedRooms).toEqual(['site:/ask']))

    act(() => transport.emitReconnected())

    expect(transport.joinedRooms.at(-1)).toBe('site:/ask')
  })
})
