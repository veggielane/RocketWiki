import { useEffect, useMemo, useState } from 'react'
import { PointerSampler } from '../realtime/pointerSampler'
import type { PointerPosition, PresenceTransport, PresenceViewer } from '../realtime/types'

export interface UsePresenceResult {
  viewers: PresenceViewer[]
  /** Other viewers' latest pointer positions, keyed by user id (the hub exposes no connection ids). */
  pointers: Map<string, PointerPosition>
  /** Call on every raw pointermove over the content area — cheap, throttled internally (realtime/pointerSampler.ts). */
  recordPointer: (x: number, y: number) => void
}

/**
 * design.md §8: join/leave a page's presence group, and throttle-sample
 * this viewer's own pointer while relaying others'. The effect depends on
 * `pageId`, not just mount/unmount — react-router reuses the same
 * `PageViewPage` instance across `/pages/:pageId` navigations (same route,
 * different param), so an empty dependency array would join the first
 * page, never leave it, and never join the second. That's exactly the
 * "leaked hub subscription is a live data leak" case design.md calls out:
 * a stale connection would keep broadcasting this viewer's cursor into a
 * page they've navigated away from.
 */
export function usePresence(pageId: string, transport: PresenceTransport): UsePresenceResult {
  const [viewers, setViewers] = useState<PresenceViewer[]>([])
  const [pointers, setPointers] = useState<Map<string, PointerPosition>>(new Map())

  useEffect(() => {
    let cancelled = false
    void transport.joinPage(pageId)

    const unsubscribeViewers = transport.onViewersChanged((next) => {
      if (cancelled) return
      setViewers(next)
      // Drop the cursors of anyone who has left. `pointers` was only ever
      // ADDED to — never pruned except on page change or unmount — so a viewer
      // who navigated away left their cursor and name label painted on the
      // overlay indefinitely, over content someone else was reading. The
      // viewer list IS the authority on who is present, so this is where the
      // pruning belongs.
      setPointers((prev) => {
        const present = new Set(next.map((viewer) => viewer.userId))
        if ([...prev.keys()].every((userId) => present.has(userId))) return prev
        return new Map([...prev].filter(([userId]) => present.has(userId)))
      })
    })
    const unsubscribePointers = transport.onPointerMoved((position: PointerPosition) => {
      if (cancelled) return
      setPointers((prev) => {
        const next = new Map(prev)
        next.set(position.userId, position)
        return next
      })
    })

    // Rejoin after a transient drop. `withAutomaticReconnect` gives the client
    // a NEW ConnectionId, and hub groups are keyed on connection id — so the
    // server's `OnConnectedAsync` registers the new connection while this page
    // group still holds only the dead one. The user vanishes from everyone
    // else's viewer list and stops receiving ViewersChanged/PointerMoved
    // entirely, silently, until they navigate. Nothing leaks (server-side
    // cleanup is correct) — presence simply dies. The co-edit provider already
    // rejoins on this signal; this is the same page, missing its half.
    //
    // Stale pointers go with the gap: the ones held now are from before the
    // drop, and the fresh ViewersChanged is what will prune them anyway.
    const unsubscribeReconnected = transport.onReconnected(() => {
      if (cancelled) return
      setPointers(new Map())
      void transport.joinPage(pageId)
    })

    return () => {
      cancelled = true
      unsubscribeViewers()
      unsubscribePointers()
      unsubscribeReconnected()
      void transport.leavePage(pageId)
      setViewers([])
      setPointers(new Map())
    }
  }, [pageId, transport])

  // Recreated per page, not just per transport: the hub's
  // `PointerMove(pageId, x, y)` attributes each sample to a page group, so
  // a sampler bound to a stale pageId would broadcast this viewer's cursor
  // into a page they've navigated away from.
  const sampler = useMemo(
    () => new PointerSampler((x, y) => transport.sendPointerPosition(pageId, x, y)),
    [pageId, transport],
  )

  useEffect(() => {
    sampler.start()
    return () => sampler.stop()
  }, [sampler])

  return {
    viewers,
    pointers,
    recordPointer: (x, y) => sampler.record(x, y),
  }
}
