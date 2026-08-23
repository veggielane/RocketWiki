/**
 * design.md §8: wire contracts for the SignalR hub (`/hubs/notifications`).
 * These names started as this frontend's proposal and were adopted verbatim
 * by the backend (NotificationsHub.cs documents "adopt or negotiate, never
 * silently diverge") — method names `JoinPage`/`LeavePage`/`PointerMove`,
 * event names `Notification`/`ViewersChanged`/`PointerMoved`.
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
 * Ephemeral presence state (design.md §8): "who's here" avatars and live
 * mouse pointers. Never persisted, never audited beyond the page view
 * itself, and payloads carry display name + colour only — **never**
 * attributes (nationality is sensitive, §6.2) and never content.
 *
 * Keyed by `userId`, not connection id — the hub's `ViewersChanged` and
 * `PointerMoved` payloads deliberately expose no connection ids
 * (NotificationsHub.ToPublicViews), and the colour is server-assigned so
 * every viewer sees the same one for a given user.
 */
export interface PresenceViewer {
  userId: string
  displayName: string
  colour: string
}

/**
 * One viewer's live pointer. `x`/`y` are viewport-relative fractions (0..1
 * of the content area's width/height), so a position means the same thing
 * regardless of each viewer's window size. Carries the sender's identity
 * inline (the hub attributes every relayed sample) so rendering never
 * needs to join against the viewer list.
 */
export interface PointerPosition {
  userId: string
  displayName: string
  colour: string
  x: number
  y: number
}

export interface PresenceTransport {
  /**
   * Presence uses page-scoped hub groups, not per-user fan-out (design.md
   * §8 — high-frequency + identical-for-everyone, unlike notifications).
   * Joining/leaving must be explicit and paired: a page left behind in a
   * group after the viewer navigates away is a live data leak (their
   * cursor keeps broadcasting to a page they're no longer looking at),
   * not just a resource leak.
   */
  joinPage(pageId: string): Promise<void>
  leavePage(pageId: string): Promise<void>
  onViewersChanged(handler: (viewers: PresenceViewer[]) => void): () => void
  onPointerMoved(handler: (position: PointerPosition) => void): () => void
  /**
   * Callers must throttle before calling this — the transport sends
   * whatever it's given, one message per call (see realtime/pointerSampler.ts).
   * Takes the pageId because the hub's `PointerMove(pageId, x, y)` needs to
   * know which page group to relay into (one connection can have joined
   * several pages over its lifetime).
   */
  sendPointerPosition(pageId: string, x: number, y: number): void
}
