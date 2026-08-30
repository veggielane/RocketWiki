import { useEffect, useMemo, useState } from 'react'
import { PointerSampler } from '../realtime/pointerSampler'
import type { PointerPosition, PresenceTransport, PresenceViewer } from '../realtime/types'

/** Stable empties, so a roomless screen does not hand its consumers a new array every render. */
const NOBODY: PresenceViewer[] = []
const NO_POINTERS: Map<string, PointerPosition> = new Map()

export interface UsePresenceResult {
  viewers: PresenceViewer[]
  /** Other viewers' latest pointer positions, keyed by user id (the hub exposes no connection ids). */
  pointers: Map<string, PointerPosition>
  /** Call on every raw pointermove over the content area — cheap, throttled internally (realtime/pointerSampler.ts). */
  recordPointer: (x: number, y: number) => void
}

/**
 * design.md §8: join/leave a ROOM's presence group, and throttle-sample this
 * viewer's own pointer while relaying others'.
 *
 * The effect depends on `roomKey`, not just mount. It always had to — the same
 * `PageViewPage` instance is reused across `/pages/:pageId` navigations, so an
 * empty dependency array would join the first page, never leave it, and never
 * join the second. Now that presence lives in the app shell and the room
 * follows the route, that case is not an edge any more: the shell never
 * unmounts, so leaving on room change is the ONLY thing that ever leaves a
 * room. Get it wrong and every screen a user visits accumulates, each still
 * receiving their cursor — exactly the "leaked subscription is a live data
 * leak" case design.md calls out.
 *
 * `null` means no room: page screens report it while their id is still
 * resolving, and joining a guessed room in the meantime would be worse than
 * joining none.
 */
export function usePresence(roomKey: string | null, transport: PresenceTransport): UsePresenceResult {
  const [joinedViewers, setViewers] = useState<PresenceViewer[]>([])
  const [joinedPointers, setPointers] = useState<Map<string, PointerPosition>>(new Map())

  // Derived, not cleared in an effect. With no room there is nobody to show,
  // and saying so during render means a screen without a room never paints one
  // frame of the previous room's cursors — which an effect-then-setState would
  // allow, and which is other people's cursors over content they are not on.
  const viewers = roomKey === null ? NOBODY : joinedViewers
  const pointers = roomKey === null ? NO_POINTERS : joinedPointers

  useEffect(() => {
    if (roomKey === null) return
    let cancelled = false
    void transport.joinRoom(roomKey)

    const unsubscribeViewers = transport.onViewersChanged((next) => {
      if (cancelled) return
      setViewers(next)
      // Drop the cursors of anyone who has left. `pointers` was only ever
      // ADDED to — never pruned except on room change or unmount — so a viewer
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

    // Rejoin after a transient drop, into the room being looked at NOW.
    // `withAutomaticReconnect` gives the client a NEW ConnectionId, and hub
    // groups are keyed on connection id — so the server's `OnConnectedAsync`
    // registers the new connection while the room group still holds only the
    // dead one. The user vanishes from everyone else's viewer list and stops
    // receiving ViewersChanged/PointerMoved entirely, silently, until they
    // navigate. Nothing leaks (server-side cleanup is correct) — presence
    // simply dies. This closure captures the room the effect ran for, and the
    // effect re-runs on every room change, so a reconnect can never rejoin a
    // room the user has already left.
    //
    // Stale pointers go with the gap: the ones held now are from before the
    // drop, and the fresh ViewersChanged is what will prune them anyway.
    const unsubscribeReconnected = transport.onReconnected(() => {
      if (cancelled) return
      setPointers(new Map())
      void transport.joinRoom(roomKey)
    })

    return () => {
      cancelled = true
      unsubscribeViewers()
      unsubscribePointers()
      unsubscribeReconnected()
      void transport.leaveRoom(roomKey)
      setViewers([])
      setPointers(new Map())
    }
  }, [roomKey, transport])

  // Recreated per room, not just per transport: the hub's
  // `PointerMove(roomKey, x, y)` attributes each sample to a group, so a
  // sampler bound to a stale room would broadcast this viewer's cursor into a
  // screen they have navigated away from.
  const sampler = useMemo(
    () => new PointerSampler((x, y) => (roomKey === null ? undefined : transport.sendPointerPosition(roomKey, x, y))),
    [roomKey, transport],
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
