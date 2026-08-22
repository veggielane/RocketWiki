import { useEffect, useMemo, useState } from 'react'
import type { NotificationPayload, NotificationsTransport } from '../realtime/types'

export interface UseNotificationsResult {
  notifications: NotificationPayload[]
  unreadCount: number
  markRead: (id: string) => void
}

/**
 * Merges persisted rows (an offline user's catch-up list, design.md §8)
 * with live pushes from the transport. Connects once per transport
 * instance and disconnects on unmount/transport change — a live
 * subscription outliving its component is exactly the "not just a memory
 * leak" case design.md calls out for presence, and the same reasoning
 * applies here.
 */
export function useNotifications(
  transport: NotificationsTransport,
  persisted: NotificationPayload[],
): UseNotificationsResult {
  const [live, setLive] = useState<NotificationPayload[]>([])
  const [readIds, setReadIds] = useState<Set<string>>(() => new Set())

  useEffect(() => {
    let cancelled = false
    void transport.connect()
    const unsubscribe = transport.onNotification((notification) => {
      if (cancelled) return
      setLive((prev) => [notification, ...prev])
    })
    return () => {
      cancelled = true
      unsubscribe()
      void transport.disconnect()
    }
  }, [transport])

  // Live pushes take precedence over a persisted row for the same id
  // (e.g. one arrives live, then a later `notifications` refetch includes
  // its persisted copy) — de-duplicated by id, newest-first.
  const notifications = useMemo(() => {
    const byId = new Map<string, NotificationPayload>()
    for (const n of [...live, ...persisted]) {
      if (!byId.has(n.id)) byId.set(n.id, n)
    }
    return [...byId.values()].sort((a, b) => b.timestampUtc.localeCompare(a.timestampUtc))
  }, [live, persisted])

  const unreadCount = notifications.filter((n) => !n.readAtUtc && !readIds.has(n.id)).length

  const markRead = (id: string) => {
    setReadIds((prev) => new Set(prev).add(id))
  }

  return { notifications, unreadCount, markRead }
}
