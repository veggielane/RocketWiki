import { createContext, useContext, useEffect } from 'react'
import type { PresenceViewer } from '../realtime/types'

export interface PresenceRoomValue {
  /** Everyone in the current room, for a screen that wants to show them. */
  viewers: PresenceViewer[]
  /**
   * Overrides the room derived from the route. Identity-stable, so a screen can
   * depend on it in an effect without re-firing every render.
   */
  setRoom: (room: string) => void
}

/**
 * The shell owns presence; screens contribute two things to it.
 *
 * Presence used to be mounted per page, which is why it only existed on two
 * screens. It now lives in AppShell so every route has it, and this is how the
 * two directions still flow: a page screen pushes UP the room only it can know
 * (`page:{id}`, resolved from a slug or a route param), and any screen pulls
 * DOWN the viewer list to render its own avatars.
 *
 * Used directly as a provider (`<PresenceRoomContext value={…}>`), the same way
 * `PageIdContext` and `PageTitleContext` are — a wrapper component would make
 * this a file that exports both a component and hooks, which costs the hooks
 * their Fast Refresh.
 *
 * The default is inert rather than throwing: page screens and `PresenceAvatars`
 * are rendered directly by tests and by the preview capture suite, neither of
 * which mounts a shell, and neither should have to.
 */
export const PresenceRoomContext = createContext<PresenceRoomValue>({ viewers: [], setRoom: () => {} })

/** Everyone the shell currently sees in this screen's room. */
export function usePresenceViewers(): PresenceViewer[] {
  return useContext(PresenceRoomContext).viewers
}

/**
 * Declares the room for a screen the route cannot name — in practice every page
 * screen, whose id may only be known after a slug has been resolved.
 *
 * `null` while the id is still loading, which leaves the shell in no room at
 * all rather than in a guessed one. Joining a room derived from the URL and
 * then moving to the real one would put two rooms' worth of join/leave traffic
 * on every page navigation, and would briefly place a reader in a room named
 * after a slug that nobody else's URL agrees on.
 */
export function useSetPresenceRoom(room: string | null): void {
  const { setRoom } = useContext(PresenceRoomContext)
  useEffect(() => {
    if (room !== null) setRoom(room)
  }, [room, setRoom])
}
