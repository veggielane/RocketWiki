import { describe, expect, it } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import { usePresence } from '../usePresence'
import { FakePresenceTransport } from '../../realtime/FakePresenceTransport'

describe('usePresence', () => {
  it('joins the given page on mount', () => {
    const transport = new FakePresenceTransport()
    renderHook(() => usePresence('page-1', transport))
    expect(transport.currentlyJoinedPages).toEqual(['page-1'])
  })

  it('leaves the page on unmount', () => {
    const transport = new FakePresenceTransport()
    const { unmount } = renderHook(() => usePresence('page-1', transport))
    unmount()
    expect(transport.currentlyJoinedPages).toEqual([])
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
    expect(transport.currentlyJoinedPages).toEqual(['page-1'])

    rerender({ pageId: 'page-2' })

    expect(transport.currentlyJoinedPages).toEqual(['page-2'])
    expect(transport.leftPages).toContain('page-1')
  })

  it('updates viewers when the transport broadcasts a change', () => {
    const transport = new FakePresenceTransport()
    const { result } = renderHook(() => usePresence('page-1', transport))

    act(() => {
      transport.emitViewers([{ userId: 'u1', displayName: 'Ada', colour: '#f00', hasAvatar: false }])
    })

    expect(result.current.viewers).toEqual([{ userId: 'u1', displayName: 'Ada', colour: '#f00', hasAvatar: false }])
  })

  it('resets viewers and pointers to empty when leaving a page — stale presence from the old page must not bleed into the new one', () => {
    const transport = new FakePresenceTransport()
    const { result, rerender } = renderHook(({ pageId }) => usePresence(pageId, transport), {
      initialProps: { pageId: 'page-1' },
    })

    act(() => {
      transport.emitViewers([{ userId: 'u1', displayName: 'Ada', colour: '#f00', hasAvatar: false }])
      transport.emitPointer({ userId: 'u1', displayName: 'Ada', colour: '#f00', x: 0.5, y: 0.5 })
    })
    expect(result.current.viewers).toHaveLength(1)
    expect(result.current.pointers.size).toBe(1)

    rerender({ pageId: 'page-2' })

    expect(result.current.viewers).toEqual([])
    expect(result.current.pointers.size).toBe(0)
  })

  it("tracks another viewer's pointer by user id (the hub exposes no connection ids)", () => {
    const transport = new FakePresenceTransport()
    const { result } = renderHook(() => usePresence('page-1', transport))

    act(() => {
      transport.emitPointer({ userId: 'u1', displayName: 'Ada', colour: '#f00', x: 0.25, y: 0.75 })
    })

    expect(result.current.pointers.get('u1')).toEqual({
      userId: 'u1',
      displayName: 'Ada',
      colour: '#f00',
      x: 0.25,
      y: 0.75,
    })
  })

  it('recordPointer does not call sendPointerPosition synchronously — it goes through the throttled sampler, not straight to the transport', () => {
    const transport = new FakePresenceTransport()
    const { result } = renderHook(() => usePresence('page-1', transport))

    result.current.recordPointer(1, 2)

    expect(transport.sentPositions).toEqual([])
  })

  it('stamps sent pointer positions with the page they belong to — the hub needs the page group per sample', async () => {
    const transport = new FakePresenceTransport()
    const { result } = renderHook(() => usePresence('page-1', transport))

    await act(async () => {
      result.current.recordPointer(0.5, 0.5)
      // The sampler flushes on its ~50ms cadence.
      await new Promise((resolve) => setTimeout(resolve, 80))
    })

    expect(transport.sentPositions).toEqual([{ pageId: 'page-1', x: 0.5, y: 0.5 }])
  })

  it('does not leave a page it never joined when unmounted before any effect ran twice (no duplicate leave calls)', () => {
    const transport = new FakePresenceTransport()
    const { unmount } = renderHook(() => usePresence('page-1', transport))
    unmount()
    expect(transport.leftPages).toEqual(['page-1'])
  })
})

/**
 * Presence after a transient drop.
 *
 * `withAutomaticReconnect` comes back on a NEW ConnectionId, and hub groups are
 * keyed on connection id — so the page group still holds only the dead one. The
 * viewer disappears from everyone else's list and receives no further
 * ViewersChanged/PointerMoved, silently, until they navigate. Nothing leaks
 * (server-side cleanup is correct); presence just dies. The co-edit provider
 * already rejoined on this signal — this half was simply missing.
 */
describe('usePresence — rejoin after reconnect', () => {
  it('rejoins the page when the connection comes back', () => {
    const transport = new FakePresenceTransport()
    renderHook(() => usePresence('page-1', transport))
    expect(transport.joinedPages).toEqual(['page-1'])

    act(() => transport.emitReconnected())

    expect(transport.joinedPages).toEqual(['page-1', 'page-1'])
    // A rejoin is not a leave — the old connection is already gone server-side.
    expect(transport.leftPages).toEqual([])
  })

  it('rejoins the page currently being viewed, not the one joined at mount', () => {
    const transport = new FakePresenceTransport()
    const { rerender } = renderHook(({ pageId }) => usePresence(pageId, transport), {
      initialProps: { pageId: 'page-1' },
    })
    rerender({ pageId: 'page-2' })
    const before = transport.joinedPages.length

    act(() => transport.emitReconnected())

    expect(transport.joinedPages).toHaveLength(before + 1)
    expect(transport.joinedPages.at(-1)).toBe('page-2')
  })

  it('drops the pointers held from before the drop', () => {
    // They are positions from a connection that no longer exists; the fresh
    // ViewersChanged that follows the rejoin is the authority.
    const transport = new FakePresenceTransport()
    const { result } = renderHook(() => usePresence('page-1', transport))
    act(() => {
      transport.emitViewers([{ userId: 'u1', displayName: 'Ada', colour: '#f00', hasAvatar: false }])
      transport.emitPointer({ userId: 'u1', displayName: 'Ada', colour: '#f00', x: 0.5, y: 0.5 })
    })
    expect(result.current.pointers.size).toBe(1)

    act(() => transport.emitReconnected())

    expect(result.current.pointers.size).toBe(0)
  })

  it('stops rejoining once unmounted — a torn-down page must not come back', () => {
    const transport = new FakePresenceTransport()
    const { unmount } = renderHook(() => usePresence('page-1', transport))
    unmount()
    const joinsAfterUnmount = transport.joinedPages.length

    act(() => transport.emitReconnected())

    expect(transport.joinedPages).toHaveLength(joinsAfterUnmount)
  })
})
