/**
 * design.md §8: wire contracts for the SignalR hub (`/hubs/notifications`).
 * These names started as this frontend's proposal and were adopted verbatim
 * by the backend (NotificationsHub.cs documents "adopt or negotiate, never
 * silently diverge") — method names `JoinPage`/`LeavePage` (now
 * `JoinRoom`/`LeaveRoom`), event names `Notification`/`ViewersChanged`.
 */

/**
 * A live push, or a persisted row fetched to let an offline user catch up
 * (design.md §8: "Notifications are also persisted... so users who were
 * offline catch up; SignalR delivers the live nudge, the table is the
 * record"). Payloads are minimal by design — never content, never diffs —
 * and `pageTitle` is null whenever the recipient's `canView` no longer
 * holds by delivery time, which is the normal case, not an error: a
 * restriction can change between when a notification was queued and when
 * it's read, and the server evaluates `canView` fresh at send time (§8).
 * Rendering code must treat a null title as "can't show this," never
 * attempt to backfill it from another source, and never treat its absence
 * as exceptional. `pageId`/`spaceKey` are nullable to match the persisted
 * rows (the server nulls what the recipient may no longer see).
 */
export interface NotificationPayload {
  id: string
  type: NotificationType
  pageId: string | null
  spaceKey: string | null
  pageTitle: string | null
  /** Null for rows produced by a process rather than a person (sync imports) — rendered actor-less. */
  actorDisplayName: string | null
  timestampUtc: string
  readAtUtc: string | null
}

export type NotificationType = 'page_watched_changed' | 'comment_reply' | 'mention' | 'sync_bundle_landed'

export interface NotificationsTransport {
  connect(): Promise<void>
  disconnect(): Promise<void>
  /** Returns an unsubscribe function. */
  onNotification(handler: (notification: NotificationPayload) => void): () => void
}

/**
 * Ephemeral presence state (design.md §8): the "who's here" avatars. Never
 * persisted, never audited beyond the page view itself, and payloads carry
 * display name + colour only — **never** attributes (nationality is
 * sensitive, §6.2) and never content.
 *
 * Keyed by `userId`, not connection id — the hub's `ViewersChanged` payload
 * deliberately exposes no connection ids (NotificationsHub.ToPublicViews),
 * and the colour is server-assigned so every viewer sees the same one for a
 * given user.
 */
export interface PresenceViewer {
  userId: string
  displayName: string
  colour: string
  /**
   * Whether this viewer has an uploaded avatar. Supplied by the hub so
   * `UserAvatar` can render initials without probing `GET /users/{id}/avatar`
   * — omitting it meant a 404 on every page view for every viewer who had
   * never uploaded one.
   */
  hasAvatar: boolean
}

/**
 * What `JoinEditSession` returns to an authorized member (design.md §8
 * "CRDT co-editing"; NotificationsHub.EditSessions.cs adopted this shape).
 * `role: 'seeder'` means: build the Y.Doc from the page's saved content at
 * `baseRevisionNumber` and push the encoded full state as your first
 * update. `role: 'joiner'` means: start from an empty Y.Doc and apply
 * `updateLog` in order — an empty log means the seed arrives as a live
 * `UpdateReceived`. `baseRevisionNumber` is the `expectedRevisionNumber`
 * for the session's next `updatePageContent` save.
 */
export interface EditSessionJoin {
  role: 'seeder' | 'joiner'
  baseRevisionNumber: number
  updateLog: Uint8Array[]
}

export type ReseedReason = 'log_cap' | 'seeder_lost'

/**
 * The Yjs relay half of the hub (design.md §8): opaque binary updates and
 * awareness (caret/selection) payloads between edit-session members, on the
 * SAME connection as presence — the hub is one connection per client, and
 * co-editing must never open a second one. Implemented by
 * SignalRPresenceTransport (which therefore serves both interfaces) and by
 * FakePresenceTransport for tests/`VITE_FAKE_REALTIME`.
 *
 * Refusals are silent by design: `joinEditSession` resolves `null` for
 * "no such page", "no canView", "no canEdit" and "replica" alike — the
 * caller renders the solo editing path, never an error state, because a
 * refusal is indistinguishable from nonexistence (design.md §6.7).
 */
export interface CoEditTransport {
  /** Resolves null when refused (or when this transport doesn't do co-editing at all — the fake). */
  joinEditSession(pageId: string): Promise<EditSessionJoin | null>
  /** Like `leavePage`: must never throw from a teardown path. */
  leaveEditSession(pageId: string): Promise<void>
  /**
   * One Yjs update out. Callers batch before calling (the provider merges
   * keystroke-level updates on a short debounce) — the server caps a
   * single update at 512 KiB and silently drops larger ones.
   */
  pushUpdate(pageId: string, update: Uint8Array): Promise<void>
  /** Yjs awareness protocol bytes out — ephemeral, throttled by the caller (~10–15/s), capped at 16 KiB. */
  pushAwareness(pageId: string, awarenessUpdate: Uint8Array): void
  /** The log-cap handback: the full encoded Y.Doc state that replaces the server's update log (≤ 4 MiB). */
  reseedEditSession(pageId: string, fullState: Uint8Array): Promise<void>
  onUpdateReceived(handler: (pageId: string, update: Uint8Array) => void): () => void
  onAwarenessReceived(handler: (pageId: string, awarenessUpdate: Uint8Array) => void): () => void
  onReseedRequired(handler: (pageId: string, baseRevisionNumber: number, reason: ReseedReason) => void): () => void
  /** canEdit was revoked mid-session: tear down, drop to read-only, do NOT retry-join. */
  onEvictedFromEditSession(handler: (pageId: string) => void): () => void
  /** Fires after the underlying connection auto-reconnects — the provider rejoins and replays. */
  onReconnected(handler: () => void): () => void
}

export interface PresenceTransport {
  /**
   * Presence uses ROOM-scoped hub groups, not per-user fan-out (design.md
   * §8 — high-frequency + identical-for-everyone, unlike notifications).
   *
   * A room is an opaque authorized string (`page:{id}`, `space:{KEY}:{screen}`,
   * `site:{path}` — see presence/presenceRoom.ts), because presence follows the
   * SCREEN and not every screen is a page. The server decides whether the caller
   * may join a given room; the client never assumes it may.
   *
   * Joining/leaving must be explicit and paired, and that now matters on every
   * navigation rather than only between pages: a room left behind after the
   * viewer has moved on is a live data leak — they stay listed as present on
   * a screen they are no longer looking at — not just a resource leak.
   */
  joinRoom(roomKey: string): Promise<void>
  leaveRoom(roomKey: string): Promise<void>
  onViewersChanged(handler: (viewers: PresenceViewer[]) => void): () => void
  /**
   * Fires after the underlying connection auto-reconnects.
   *
   * Required, not optional, because presence cannot be correct without it.
   * `withAutomaticReconnect` comes back with a NEW ConnectionId and hub groups
   * are keyed on connection id, so a page group still holds only the dead one:
   * the viewer disappears from everyone else's list and receives nothing
   * further, silently, until they navigate. A transport that cannot report a
   * reconnect cannot support presence — unlike `onConnectionStateChanged`
   * below, where having no signal degrades to the honest default of "assume
   * up".
   */
  onReconnected(handler: () => void): () => void
  /**
   * Whether the hub is actually up. Both SignalR transports call
   * `.withAutomaticReconnect()`, and only `onreconnected` was ever registered —
   * so the app could observe RECOVERY but never LOSS. Nothing told the user the
   * connection had dropped, was retrying, or had given up, while the edit
   * screen went on rendering a green "Live co-editing" chip throughout and the
   * presence avatars froze at their last-known set with no staleness cue.
   *
   * Optional so a fake or a future transport need not implement it; a caller
   * with no signal treats the connection as up, which is the pre-existing
   * behaviour rather than a new pessimism.
   */
  onConnectionStateChanged?(handler: (state: RealtimeConnectionState) => void): () => void
}

/**
 * `reconnecting` is recoverable and usually brief; `disconnected` is SignalR
 * having exhausted its retry policy, after which nothing arrives until the page
 * is reloaded. Worth distinguishing: one is "hold on", the other is "this is
 * not coming back on its own".
 */
export type RealtimeConnectionState = 'connected' | 'reconnecting' | 'disconnected'
