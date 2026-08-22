import { useEffect, useMemo, useState } from 'react'
import { PointerSampler } from '../realtime/pointerSampler'
import type { PointerPosition, PresenceTransport, PresenceViewer } from '../realtime/types'

export interface UsePresenceResult {
  viewers: PresenceViewer[]
  /** Other viewers' latest pointer positions, keyed by connection id. */
  pointers: Map<string, { x: number; y: number }>
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
  const [pointers, setPointers] = useState<Map<string, { x: number; y: number }>>(new Map())

  useEffect(() => {
    let cancelled = false
    void transport.joinPage(pageId)

    const unsubscribeViewers = transport.onViewersChanged((next) => {
      if (!cancelled) setViewers(next)
    })
    const unsubscribePointers = transport.onPointerMoved((position: PointerPosition) => {
      if (cancelled) return
      setPointers((prev) => {
        const next = new Map(prev)
        next.set(position.connectionId, { x: position.x, y: position.y })
        return next
      })
    })

    return () => {
      cancelled = true
      unsubscribeViewers()
      unsubscribePointers()
      void transport.leavePage(pageId)
      setViewers([])
      setPointers(new Map())
    }
  }, [pageId, transport])

  const sampler = useMemo(
    () => new PointerSampler((x, y) => transport.sendPointerPosition(x, y)),
    [transport],
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
