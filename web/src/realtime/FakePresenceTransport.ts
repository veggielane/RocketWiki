import type {
  CoEditTransport,
  EditSessionJoin,
  PointerPosition,
  PresenceTransport,
  PresenceViewer,
  ReseedReason,
} from './types'

/**
 * Stands in for a real presence connection, for tests and for running the
 * SPA without a backend (`VITE_FAKE_REALTIME=true` — see
 * realtime/transports.ts). Tracks joined pages and sent pointer positions
 * so tests can assert teardown actually happens, which is the entire
 * point of this feature ("a leaked hub subscription is a live data leak,
 * not just a memory leak" — design.md §8).
 *
 * Also stands in for `CoEditTransport` (the real transport is one class on
 * one connection, so the fake mirrors that). By default `joinEditSession`
 * resolves `null` — which the provider renders as the SOLO editing path,
 * making "fake realtime = solo editing" the dev-mode behavior by
 * construction. Tests script a session by assigning `editSessionJoinResult`
 * and drive the hub side through the `emit*` helpers.
 */
export class FakePresenceTransport implements PresenceTransport, CoEditTransport {
  private viewerHandlers = new Set<(viewers: PresenceViewer[]) => void>()
  private pointerHandlers = new Set<(position: PointerPosition) => void>()
  private updateHandlers = new Set<(pageId: string, update: Uint8Array) => void>()
  private awarenessHandlers = new Set<(pageId: string, update: Uint8Array) => void>()
  private reseedHandlers = new Set<(pageId: string, baseRevisionNumber: number, reason: ReseedReason) => void>()
  private evictedHandlers = new Set<(pageId: string) => void>()
  private reconnectedHandlers = new Set<() => void>()

  joinedRooms: string[] = []
  leftRooms: string[] = []
  sentPositions: { roomKey: string; x: number; y: number }[] = []

  /** Script for `joinEditSession` — null (default) means refused → solo path. May also be a function for per-call scripting. */
  editSessionJoinResult: EditSessionJoin | null | (() => EditSessionJoin | null) = null
  joinedEditSessions: string[] = []
  leftEditSessions: string[] = []
  pushedUpdates: { pageId: string; update: Uint8Array }[] = []
  pushedAwareness: { pageId: string; update: Uint8Array }[] = []
  reseeds: { pageId: string; fullState: Uint8Array }[] = []

  async joinRoom(roomKey: string): Promise<void> {
    this.joinedRooms.push(roomKey)
  }

  async leaveRoom(roomKey: string): Promise<void> {
    this.leftRooms.push(roomKey)
  }

  /** Currently-joined rooms, accounting for leaves — for tests asserting "no room left joined after teardown." */
  get currentlyJoinedRooms(): string[] {
    const left = [...this.leftRooms]
    return this.joinedRooms.filter((roomKey) => {
      const i = left.indexOf(roomKey)
      if (i === -1) return true
      left.splice(i, 1)
      return false
    })
  }

  onViewersChanged(handler: (viewers: PresenceViewer[]) => void): () => void {
    this.viewerHandlers.add(handler)
    return () => this.viewerHandlers.delete(handler)
  }

  onPointerMoved(handler: (position: PointerPosition) => void): () => void {
    this.pointerHandlers.add(handler)
    return () => this.pointerHandlers.delete(handler)
  }

  sendPointerPosition(roomKey: string, x: number, y: number): void {
    this.sentPositions.push({ roomKey, x, y })
  }

  /** Test/dev only: simulates the hub broadcasting an updated viewer list. */
  emitViewers(viewers: PresenceViewer[]): void {
    for (const handler of this.viewerHandlers) {
      handler(viewers)
    }
  }

  /** Test/dev only: simulates another viewer's pointer position arriving. */
  emitPointer(position: PointerPosition): void {
    for (const handler of this.pointerHandlers) {
      handler(position)
    }
  }

  // ---- CoEditTransport ----

  async joinEditSession(pageId: string): Promise<EditSessionJoin | null> {
    this.joinedEditSessions.push(pageId)
    return typeof this.editSessionJoinResult === 'function'
      ? this.editSessionJoinResult()
      : this.editSessionJoinResult
  }

  async leaveEditSession(pageId: string): Promise<void> {
    this.leftEditSessions.push(pageId)
  }

  async pushUpdate(pageId: string, update: Uint8Array): Promise<void> {
    this.pushedUpdates.push({ pageId, update })
  }

  pushAwareness(pageId: string, update: Uint8Array): void {
    this.pushedAwareness.push({ pageId, update })
  }

  async reseedEditSession(pageId: string, fullState: Uint8Array): Promise<void> {
    this.reseeds.push({ pageId, fullState })
  }

  onUpdateReceived(handler: (pageId: string, update: Uint8Array) => void): () => void {
    this.updateHandlers.add(handler)
    return () => this.updateHandlers.delete(handler)
  }

  onAwarenessReceived(handler: (pageId: string, update: Uint8Array) => void): () => void {
    this.awarenessHandlers.add(handler)
    return () => this.awarenessHandlers.delete(handler)
  }

  onReseedRequired(handler: (pageId: string, baseRevisionNumber: number, reason: ReseedReason) => void): () => void {
    this.reseedHandlers.add(handler)
    return () => this.reseedHandlers.delete(handler)
  }

  onEvictedFromEditSession(handler: (pageId: string) => void): () => void {
    this.evictedHandlers.add(handler)
    return () => this.evictedHandlers.delete(handler)
  }

  onReconnected(handler: () => void): () => void {
    this.reconnectedHandlers.add(handler)
    return () => this.reconnectedHandlers.delete(handler)
  }

  /** Test only: a peer's Yjs update arriving from the hub. */
  emitUpdate(pageId: string, update: Uint8Array): void {
    for (const handler of this.updateHandlers) handler(pageId, update)
  }

  /** Test only: a peer's awareness (caret/selection) payload arriving. */
  emitAwareness(pageId: string, update: Uint8Array): void {
    for (const handler of this.awarenessHandlers) handler(pageId, update)
  }

  /** Test only: the server demanding a reseed (log_cap names this client to save-and-reseed; seeder_lost promotes it to seeder). */
  emitReseedRequired(pageId: string, baseRevisionNumber: number, reason: ReseedReason): void {
    for (const handler of this.reseedHandlers) handler(pageId, baseRevisionNumber, reason)
  }

  /** Test only: the rule-change eviction sweep cutting this client's session. */
  emitEvicted(pageId: string): void {
    for (const handler of this.evictedHandlers) handler(pageId)
  }

  /** Test only: the underlying connection dropped and auto-reconnected. */
  emitReconnected(): void {
    for (const handler of this.reconnectedHandlers) handler()
  }
}
