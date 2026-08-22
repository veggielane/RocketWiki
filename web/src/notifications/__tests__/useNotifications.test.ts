import { describe, expect, it } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import { useNotifications } from '../useNotifications'
import { FakeNotificationsTransport } from '../../realtime/FakeNotificationsTransport'
import type { NotificationPayload } from '../../realtime/types'

function notification(overrides: Partial<NotificationPayload> = {}): NotificationPayload {
  return {
    id: 'n1',
    type: 'mention',
    pageId: 'page-1',
    spaceKey: 'ENG',
    pageTitle: 'Runbook',
    actorDisplayName: 'Ada',
    timestampUtc: '2026-01-01T00:00:00Z',
    readAtUtc: null,
    ...overrides,
  }
}

describe('useNotifications', () => {
  it('starts with the persisted (catch-up) list', () => {
    const transport = new FakeNotificationsTransport()
    const persisted = [notification({ id: 'p1' })]
    const { result } = renderHook(() => useNotifications(transport, persisted))
    expect(result.current.notifications.map((n) => n.id)).toEqual(['p1'])
  })

  it('connects the transport on mount', () => {
    const transport = new FakeNotificationsTransport()
    renderHook(() => useNotifications(transport, []))
    expect(transport.connected).toBe(true)
  })

  it('adds a live push to the list and counts it as unread', () => {
    const transport = new FakeNotificationsTransport()
    const { result } = renderHook(() => useNotifications(transport, []))

    act(() => {
      transport.emit(notification({ id: 'live1' }))
    })

    expect(result.current.notifications.map((n) => n.id)).toEqual(['live1'])
    expect(result.current.unreadCount).toBe(1)
  })

  it('markRead removes a notification from the unread count without removing it from the list', () => {
    const transport = new FakeNotificationsTransport()
    const { result } = renderHook(() => useNotifications(transport, [notification({ id: 'p1' })]))

    expect(result.current.unreadCount).toBe(1)
    act(() => {
      result.current.markRead('p1')
    })
    expect(result.current.unreadCount).toBe(0)
    expect(result.current.notifications).toHaveLength(1)
  })

  it('a persisted row already marked read on the server does not count as unread', () => {
    const transport = new FakeNotificationsTransport()
    const persisted = [notification({ id: 'p1', readAtUtc: '2026-01-01T01:00:00Z' })]
    const { result } = renderHook(() => useNotifications(transport, persisted))
    expect(result.current.unreadCount).toBe(0)
  })

  it('de-duplicates by id when a live push and a persisted row share one, preferring the live copy', () => {
    const transport = new FakeNotificationsTransport()
    const persisted = [notification({ id: 'dup', actorDisplayName: 'Stale Persisted Copy' })]
    const { result } = renderHook(() => useNotifications(transport, persisted))

    act(() => {
      transport.emit(notification({ id: 'dup', actorDisplayName: 'Fresh Live Copy' }))
    })

    expect(result.current.notifications).toHaveLength(1)
    expect(result.current.notifications[0]!.actorDisplayName).toBe('Fresh Live Copy')
  })

  it('unsubscribes and disconnects the transport on unmount — a leaked subscription is a live data leak', () => {
    const transport = new FakeNotificationsTransport()
    const { unmount } = renderHook(() => useNotifications(transport, []))
    expect(transport.connected).toBe(true)

    unmount()

    expect(transport.connected).toBe(false)
    // Emitting after unmount should reach no handlers — disconnect() clears them.
    expect(() => transport.emit(notification())).not.toThrow()
  })
})
