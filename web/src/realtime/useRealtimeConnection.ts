import { useEffect, useState } from 'react'
import type { PresenceTransport, RealtimeConnectionState } from './types'

/**
 * Whether the realtime connection is actually up.
 *
 * Both SignalR transports call `.withAutomaticReconnect()` and only ever
 * registered `onreconnected`, so the app could see recovery but never loss.
 * The consequence was not a missing nicety: the edit screen kept rendering a
 * green "Live co-editing" chip right through a disconnect, presence avatars
 * froze at their last-known set with no staleness cue, and the notification
 * badge silently stopped updating. A UI that asserts liveness it cannot verify
 * is worse than one that says nothing.
 *
 * Starts `connected` and stays there for a transport that publishes no
 * state — the pre-existing behaviour, rather than a new pessimism that would
 * make every fake and test render a warning.
 */
export function useRealtimeConnection(transport: PresenceTransport): RealtimeConnectionState {
  const [state, setState] = useState<RealtimeConnectionState>('connected')

  useEffect(() => {
    // Optional on the interface: a transport without it is treated as up.
    const unsubscribe = transport.onConnectionStateChanged?.(setState)
    return unsubscribe
  }, [transport])

  return state
}
