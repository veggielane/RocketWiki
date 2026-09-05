import { describe, expect, it } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import { usePresence } from '../usePresence'
import { FakePresenceTransport } from '../../realtime/FakePresenceTransport'

describe('usePresence', () => {
  it('joins the given page on mount', () => {
    const transport = new FakePresenceTransport()
    renderHook(() => usePresence('page-1', transport))
    expect(transport.currentlyJoinedRooms).toEqual(['page-1'])
  })

  it('leaves the page on unmount', () => {
    const transport = new FakePresenceTransport()
    const { unmount } = renderHook(() => usePresence('page-1', transport))
    unmount()
    expect(transport.currentlyJoinedRooms).toEqual([])
  })

  it('leaves the old page and joins the new one when pageId changes — the route-reuse case (design.md §8)', () => {
    // react-router reuses the same PageViewPage instance across
    // /pages/:pageId navigations, so this must not rely on unmount to
    // clean up: a plain re-render with a new pageId is the real-world
    // trigger, and an empty dependency array would silently never leave
    // the first page.
    const transport = new FakePresenceTransport()
    const { rerender } = renderHook(({ pageId }) => usePresence(pageId, transport), {
      initialProps: { pageId: 'page-1' },
    })
    expect(transport.currentlyJoinedRooms).toEqual(['page-1'])

    rerender({ pageId: 'page-2' })

    expect(transport.currentlyJoinedRooms).toEqual(['page-2'])
    expect(transport.leftRooms).toContain('page-1')
  })

  it('updates viewers when the transport broadcasts a change', () => {
    const transport = new FakePresenceTransport()
    const { result } = renderHook(() => usePresence('page-1', transport))

    act(() => {
      transport.emitViewers([{ userId: 'u1', displayName: 'Ada', colour: '#f00', hasAvatar: false }])
    })

    expect(result.current.viewers).toEqual([{ userId: 'u1', displayName: 'Ada', colour: '#f00', hasAvatar: false }])
  })

  it('resets viewers to empty when leaving a page — stale presence from the old page must not bleed into the new one', () => {
    const transport = new FakePresenceTransport()
    const { result, rerender } = renderHook(({ pageId }) => usePresence(pageId, transport), {
      initialProps: { pageId: 'page-1' },
    })

    act(() => {
      transport.emitViewers([{ userId: 'u1', displayName: 'Ada', colour: '#f00', hasAvatar: false }])
    })
    expect(result.current.viewers).toHaveLength(1)

    rerender({ pageId: 'page-2' })

    expect(result.current.viewers).toEqual([])
  })

  it('does not leave a page it never joined when unmounted before any effect ran twice (no duplicate leave calls)', () => {
    const transport = new FakePresenceTransport()
    const { unmount } = renderHook(() => usePresence('page-1', transport))
    unmount()
    expect(transport.leftRooms).toEqual(['page-1'])
  })
})

/**
 * Presence after a transient drop.
 *
 * `withAutomaticReconnect` comes back on a NEW ConnectionId, and hub groups are
 * keyed on connection id — so the page group still holds only the dead one. The
 * viewer disappears from everyone else's list and receives no further
 * ViewersChanged, silently, until they navigate. Nothing leaks
 * (server-side cleanup is correct); presence just dies. The co-edit provider
 * already rejoined on this signal — this half was simply missing.
 */
describe('usePresence — rejoin after reconnect', () => {
  it('rejoins the page when the connection comes back', () => {
    const transport = new FakePresenceTransport()
    renderHook(() => usePresence('page-1', transport))
    expect(transport.joinedRooms).toEqual(['page-1'])

    act(() => transport.emitReconnected())

    expect(transport.joinedRooms).toEqual(['page-1', 'page-1'])
    // A rejoin is not a leave — the old connection is already gone server-side.
    expect(transport.leftRooms).toEqual([])
  })

  it('rejoins the page currently being viewed, not the one joined at mount', () => {
    const transport = new FakePresenceTransport()
    const { rerender } = renderHook(({ pageId }) => usePresence(pageId, transport), {
      initialProps: { pageId: 'page-1' },
    })
    rerender({ pageId: 'page-2' })
    const before = transport.joinedRooms.length

    act(() => transport.emitReconnected())

    expect(transport.joinedRooms).toHaveLength(before + 1)
    expect(transport.joinedRooms.at(-1)).toBe('page-2')
  })

  it('stops rejoining once unmounted — a torn-down page must not come back', () => {
    const transport = new FakePresenceTransport()
    const { unmount } = renderHook(() => usePresence('page-1', transport))
    unmount()
    const joinsAfterUnmount = transport.joinedRooms.length

    act(() => transport.emitReconnected())

    expect(transport.joinedRooms).toHaveLength(joinsAfterUnmount)
  })
})
