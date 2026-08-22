/**
 * design.md §8: the SignalR hub (`/hubs/notifications`) doesn't exist yet
 * (milestone 4b), so these are the wire contracts a real implementation
 * must satisfy — deliberately documented before the hub is built, not
 * reverse-engineered from it afterward.
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
 * as exceptional.
 */
export interface NotificationPayload {
  id: string
  type: NotificationType
  pageId: string
  spaceKey: string
  pageTitle: string | null
  actorDisplayName: string
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
 */
export interface PresenceViewer {
  connectionId: string
  userId: string
  displayName: string
  colour: string
}

/** Viewport-relative fractions (0..1 of the content area's width/height), so a pointer position means the same thing regardless of each viewer's window size. */
export interface PointerPosition {
  connectionId: string
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
  /** Callers must throttle before calling this — the transport sends whatever it's given, one message per call (see realtime/pointerSampler.ts). */
  sendPointerPosition(x: number, y: number): void
}
