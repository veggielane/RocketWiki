import { useEffect, useState } from 'react'
import type { PresenceTransport, PresenceViewer } from '../realtime/types'

/** Stable empty, so a roomless screen does not hand its consumers a new array every render. */
const NOBODY: PresenceViewer[] = []

export interface UsePresenceResult {
  viewers: PresenceViewer[]
}

/**
 * design.md §8: join/leave a ROOM's presence group and relay who else is in it.
 *
 * The effect depends on `roomKey`, not just mount. It always had to — the same
 * `PageViewPage` instance is reused across `/pages/:pageId` navigations, so an
 * empty dependency array would join the first page, never leave it, and never
 * join the second. Now that presence lives in the app shell and the room
 * follows the route, that case is not an edge any more: the shell never
 * unmounts, so leaving on room change is the ONLY thing that ever leaves a
 * room. Get it wrong and every screen a user visits accumulates, each still
 * listing them as present — exactly the "leaked subscription is a live data
 * leak" case design.md calls out.
 *
 * `null` means no room: page screens report it while their id is still
 * resolving, and joining a guessed room in the meantime would be worse than
 * joining none.
 */
export function usePresence(roomKey: string | null, transport: PresenceTransport): UsePresenceResult {
  const [joinedViewers, setViewers] = useState<PresenceViewer[]>([])

  // Derived, not cleared in an effect. With no room there is nobody to show,
  // and saying so during render means a screen without a room never paints one
  // frame of the previous room's viewers — which an effect-then-setState would
  // allow, and which is other people listed against content they are not on.
  const viewers = roomKey === null ? NOBODY : joinedViewers

  useEffect(() => {
    if (roomKey === null) return
    let cancelled = false
    void transport.joinRoom(roomKey)

    const unsubscribeViewers = transport.onViewersChanged((next) => {
      if (cancelled) return
      setViewers(next)
    })

    // Rejoin after a transient drop, into the room being looked at NOW.
    // `withAutomaticReconnect` gives the client a NEW ConnectionId, and hub
    // groups are keyed on connection id — so the server's `OnConnectedAsync`
    // registers the new connection while the room group still holds only the
    // dead one. The user vanishes from everyone else's viewer list and stops
    // receiving ViewersChanged entirely, silently, until they navigate. Nothing
    // leaks (server-side cleanup is correct) — presence simply dies. This
    // closure captures the room the effect ran for, and the effect re-runs on
    // every room change, so a reconnect can never rejoin a room the user has
    // already left.
    const unsubscribeReconnected = transport.onReconnected(() => {
      if (cancelled) return
      void transport.joinRoom(roomKey)
    })

    return () => {
      cancelled = true
      unsubscribeViewers()
      unsubscribeReconnected()
      void transport.leaveRoom(roomKey)
      setViewers([])
    }
  }, [roomKey, transport])

  return { viewers }
}
