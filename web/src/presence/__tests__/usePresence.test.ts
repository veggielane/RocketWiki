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
      transport.emitViewers([{ connectionId: 'c1', userId: 'u1', displayName: 'Ada', colour: '#f00' }])
    })

    expect(result.current.viewers).toEqual([
      { connectionId: 'c1', userId: 'u1', displayName: 'Ada', colour: '#f00' },
    ])
  })

  it('resets viewers and pointers to empty when leaving a page — stale presence from the old page must not bleed into the new one', () => {
    const transport = new FakePresenceTransport()
    const { result, rerender } = renderHook(({ pageId }) => usePresence(pageId, transport), {
      initialProps: { pageId: 'page-1' },
    })

    act(() => {
      transport.emitViewers([{ connectionId: 'c1', userId: 'u1', displayName: 'Ada', colour: '#f00' }])
      transport.emitPointer({ connectionId: 'c1', x: 0.5, y: 0.5 })
    })
    expect(result.current.viewers).toHaveLength(1)
    expect(result.current.pointers.size).toBe(1)

    rerender({ pageId: 'page-2' })

    expect(result.current.viewers).toEqual([])
    expect(result.current.pointers.size).toBe(0)
  })

  it('tracks another viewer\'s pointer by connection id', () => {
    const transport = new FakePresenceTransport()
    const { result } = renderHook(() => usePresence('page-1', transport))

    act(() => {
      transport.emitPointer({ connectionId: 'c1', x: 0.25, y: 0.75 })
    })

    expect(result.current.pointers.get('c1')).toEqual({ x: 0.25, y: 0.75 })
  })

  it('recordPointer does not call sendPointerPosition synchronously — it goes through the throttled sampler, not straight to the transport', () => {
    const transport = new FakePresenceTransport()
    const { result } = renderHook(() => usePresence('page-1', transport))

    result.current.recordPointer(1, 2)

    expect(transport.sentPositions).toEqual([])
  })

  it('does not leave a page it never joined when unmounted before any effect ran twice (no duplicate leave calls)', () => {
    const transport = new FakePresenceTransport()
    const { unmount } = renderHook(() => usePresence('page-1', transport))
    unmount()
    expect(transport.leftPages).toEqual(['page-1'])
  })
})
