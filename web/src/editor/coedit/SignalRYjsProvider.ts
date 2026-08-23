import * as Y from 'yjs'
import { Awareness, applyAwarenessUpdate, encodeAwarenessUpdate, removeAwarenessStates } from 'y-protocols/awareness'
import type { CoEditTransport, EditSessionJoin } from '../../realtime/types'

/**
 * `connecting` → (`collaborating` | `solo`); `collaborating` → `evicted`.
 * `solo` and `evicted` are terminal for a given provider instance: solo
 * means the join was refused (or the hub is unreachable) and the page runs
 * the plain editor; evicted means canEdit was revoked mid-session — the
 * editor drops to read-only and never retry-joins.
 */
export type CoEditStatus = 'connecting' | 'collaborating' | 'solo' | 'evicted'

export interface SignalRYjsProviderOptions {
  pageId: string
  transport: CoEditTransport
  /**
   * Seeder duty (design.md §8): populate the doc's `'default'` fragment
   * from the page's saved content. Called at most once, before the first
   * full-state push — see `seedDoc.ts` for the Markdown → Y.Doc conversion
   * this delegates (the provider itself is schema-agnostic). Implementations
   * MUST apply their changes with `origin` as the Yjs transaction origin so
   * the seed rides the explicit full-state push instead of also being
   * queued as an ordinary outgoing update.
   */
  seed: (doc: Y.Doc, origin: unknown) => void
  /**
   * The log-cap flow (`ReseedRequired` with reason `log_cap`): the server
   * designated THIS client to save via `updatePageContent` and then hand
   * back a full-state snapshot. The app performs the save and calls
   * `completeReseed(newRevisionNumber)`; until then the demand stays
   * pending (`pendingReseed`).
   */
  onSaveAndReseedRequired?: (baseRevisionNumber: number) => void
  onStatusChange?: (status: CoEditStatus) => void
  onBaseRevisionChange?: (baseRevisionNumber: number) => void
  /**
   * A single Yjs update too large for the relay's 512 KiB cap (a huge
   * paste in one transaction). The server drops oversized frames silently;
   * this is the honest local signal so the UI can tell the author their
   * change is not reaching peers live and a save is needed.
   */
  onOversizedUpdate?: () => void
  /** Debounce for outgoing doc updates (batches keystrokes into one merged push). */
  updateFlushMs?: number
  /** Throttle tick for outgoing awareness (caret/selection) — ~12/s at the default. */
  awarenessFlushMs?: number
  /** How long to wait for the join before falling back to solo (hub unreachable ≠ broken editor). */
  joinTimeoutMs?: number
}

/** Server-enforced cap on one relayed update (CoEditOptions.UpdateMaxBytes); oversized frames are dropped silently server-side. */
const UPDATE_MAX_BYTES = 512 * 1024

/**
 * A Yjs provider over the app's existing SignalR hub connection (design.md
 * §8 "CRDT co-editing"; protocol in NotificationsHub.EditSessions.cs). One
 * instance per edit session: owns a `Y.Doc` + `Awareness` pair, joins the
 * session, plays whichever role the server assigns (seeder builds the doc
 * from saved content; joiner replays the update log), then bridges doc
 * updates and awareness both ways — batched/throttled out, applied in.
 *
 * The transport relays; it never interprets. Everything CRDT-shaped
 * (merge, idempotence, awareness encoding) happens here with the real Yjs
 * libraries, which is also why reconnect can leans on Yjs semantics:
 * updates are commutative and idempotent under merge, so replaying a log
 * we partially know is safe by construction.
 *
 * ## What reconnect preserves — honestly
 *
 * On hub reconnect the provider rejoins and applies the fresh join result
 * into its EXISTING doc, then pushes its own full state:
 *
 * - **Same session survived** (the server keeps sessions through a grace
 *   window): the replayed log shares history with our doc, so applying it
 *   is an idempotent merge — we gain whatever peers did while we were
 *   gone, they gain our offline edits from the full-state push. Nothing
 *   is lost.
 * - **Session was dropped and WE are re-designated seeder**: we seed from
 *   our own live doc (not from saved content — ours is a superset that
 *   includes every unsaved live edit we ever merged). Nothing is lost.
 * - **Session was dropped and a RECONNECTING peer reseeded first**: their
 *   doc shares CRDT lineage with ours (same original seed), so the replay
 *   is again a clean merge. Nothing is lost.
 * - **Session was dropped and a BRAND-NEW client seeded from saved
 *   content before we reconnected**: their seed has no shared history with
 *   our doc, and merging two independent encodings of the same text
 *   duplicates it. Yjs cannot deduplicate semantically-identical but
 *   historically-unrelated content — no provider can. v1 accepts this
 *   corner (it needs an API restart or session expiry AND a fresh joiner
 *   racing ahead of every reconnecting member); the duplication is visible
 *   in the editor, fixable by normal editing, and the save path with its
 *   StaleRevision flow remains the durable safety net.
 *
 * If the rejoin is refused (canEdit revoked while offline), the provider
 * lands in `evicted` — read-only, no retry loop — the same terminal state
 * as an explicit `EvictedFromEditSession`.
 */
export class SignalRYjsProvider {
  readonly doc: Y.Doc
  /** Read by CollaborationCaret via `provider.awareness` — the only thing it needs from a provider. */
  readonly awareness: Awareness

  private readonly options: SignalRYjsProviderOptions
  private statusValue: CoEditStatus = 'connecting'
  private baseRevision: number | null = null
  private pendingReseedBase: number | null = null
  private seeded = false
  private disposed = false

  private pendingUpdates: Uint8Array[] = []
  private updateFlushTimer: ReturnType<typeof setTimeout> | null = null
  private dirtyAwarenessClients = new Set<number>()
  private awarenessFlushTimer: ReturnType<typeof setTimeout> | null = null

  private unsubscribers: (() => void)[] = []

  constructor(options: SignalRYjsProviderOptions) {
    this.options = options
    this.doc = new Y.Doc()
    this.awareness = new Awareness(this.doc)
  }

  get status(): CoEditStatus {
    return this.statusValue
  }

  /** The `expectedRevisionNumber` for the session's next save — from the join, advanced by saves and reseeds. */
  get baseRevisionNumber(): number | null {
    return this.baseRevision
  }

  /** Non-null while the server has demanded a save-and-reseed from this client (log_cap). */
  get pendingReseed(): number | null {
    return this.pendingReseedBase
  }

  /**
   * Join and start bridging. Resolves once the session state is known —
   * callers must not mount a collaborative editor before then (the
   * Collaboration extension replaces the editor's own history, so the
   * collab-or-solo decision has to precede editor creation).
   */
  async connect(): Promise<void> {
    const { transport, pageId, joinTimeoutMs = 4000 } = this.options

    let result: EditSessionJoin | null
    try {
      result = await this.joinWithTimeout(joinTimeoutMs)
    } catch {
      // Hub unreachable / join failed / timed out: co-editing is a
      // progressive enhancement — the solo editor is the fallback, never
      // an error state (the refusal is indistinguishable from
      // nonexistence by design, §6.7).
      this.setStatus('solo')
      return
    }

    if (this.disposed) {
      // Disposed while the join was in flight — if it actually succeeded,
      // withdraw so we don't linger as a ghost member of the session.
      if (result) void transport.leaveEditSession(pageId)
      return
    }

    if (result === null) {
      this.setStatus('solo')
      return
    }

    this.baseRevision = result.baseRevisionNumber
    this.options.onBaseRevisionChange?.(result.baseRevisionNumber)
    this.subscribe()

    if (result.role === 'seeder') {
      // Seeder: build the doc from the page's saved content and push the
      // encoded full state as the session's first update/log entry.
      this.options.seed(this.doc, this)
      this.seeded = true
      await this.pushFullState()
    } else {
      // Joiner: replay the log in order. An empty log means the seed
      // arrives as a live UpdateReceived (or as a seeder_lost promotion).
      for (const update of result.updateLog) {
        Y.applyUpdate(this.doc, update, this)
      }
      this.seeded = result.updateLog.length > 0
    }

    this.setStatus('collaborating')
  }

  /**
   * The second half of the log-cap flow: the app saved via
   * `updatePageContent`; hand the server one full-state snapshot to
   * replace its log, and advance the session base. (The server reads the
   * new base from the page row itself — our number is for local tracking
   * only, the no-client-claims rule.)
   */
  async completeReseed(newRevisionNumber: number): Promise<void> {
    if (this.disposed || this.statusValue !== 'collaborating') return
    this.pendingReseedBase = null
    this.noteSaved(newRevisionNumber)
    try {
      await this.options.transport.reseedEditSession(this.options.pageId, Y.encodeStateAsUpdate(this.doc))
    } catch {
      // The connection dropped mid-handback; the server keeps the demand
      // pending and the reconnect path pushes full state anyway.
    }
  }

  /** A session save landed (any reason): the session's base advances — the server advances its copy too. */
  noteSaved(newRevisionNumber: number): void {
    this.baseRevision = newRevisionNumber
    this.options.onBaseRevisionChange?.(newRevisionNumber)
  }

  /**
   * Route-away/unmount teardown: flush what we owe the session, withdraw
   * our awareness state (so peers drop our caret immediately instead of
   * waiting for a timeout), and leave. Leaving must be explicit — the
   * connection outlives the page, so the server can't infer departure
   * from a disconnect (the same reason LeavePage exists).
   */
  dispose(): void {
    if (this.disposed) return
    const wasCollaborating = this.statusValue === 'collaborating'
    this.disposed = true

    if (wasCollaborating) {
      this.flushUpdates()
      removeAwarenessStates(this.awareness, [this.doc.clientID], 'dispose')
      this.flushAwareness(true)
      void this.options.transport.leaveEditSession(this.options.pageId)
    }

    this.teardown()
    this.awareness.destroy()
    this.doc.destroy()
  }

  // ---- wiring ----

  private subscribe(): void {
    const { transport, pageId } = this.options

    this.doc.on('update', this.handleDocUpdate)
    this.awareness.on('update', this.handleAwarenessUpdate)
    this.unsubscribers.push(
      () => this.doc.off('update', this.handleDocUpdate),
      () => this.awareness.off('update', this.handleAwarenessUpdate),
      transport.onUpdateReceived((incomingPageId, update) => {
        if (incomingPageId !== pageId || this.disposed) return
        Y.applyUpdate(this.doc, update, this)
      }),
      transport.onAwarenessReceived((incomingPageId, update) => {
        if (incomingPageId !== pageId || this.disposed) return
        applyAwarenessUpdate(this.awareness, update, this)
      }),
      transport.onReseedRequired((incomingPageId, baseRevisionNumber, reason) => {
        if (incomingPageId !== pageId || this.disposed) return
        if (reason === 'seeder_lost') {
          this.handleSeederLost()
        } else {
          this.pendingReseedBase = baseRevisionNumber
          this.options.onSaveAndReseedRequired?.(baseRevisionNumber)
        }
      }),
      transport.onEvictedFromEditSession((incomingPageId) => {
        if (incomingPageId !== pageId || this.disposed) return
        this.handleEvicted()
      }),
      transport.onReconnected(() => {
        if (this.disposed || this.statusValue !== 'collaborating') return
        void this.rejoinAfterReconnect()
      }),
    )
  }

  private teardown(): void {
    for (const unsubscribe of this.unsubscribers) unsubscribe()
    this.unsubscribers = []
    if (this.updateFlushTimer) {
      clearTimeout(this.updateFlushTimer)
      this.updateFlushTimer = null
    }
    if (this.awarenessFlushTimer) {
      clearTimeout(this.awarenessFlushTimer)
      this.awarenessFlushTimer = null
    }
    this.pendingUpdates = []
    this.dirtyAwarenessClients.clear()
  }

  // Arrow property, not a method: `doc.on` gets a stable reference that
  // `doc.off` can remove in teardown.
  private readonly handleDocUpdate = (update: Uint8Array, origin: unknown): void => {
    // Updates we applied ourselves (remote, replay) carry `this` as
    // origin: relaying them back would echo every peer's edit around the
    // session forever.
    if (origin === this || this.disposed || this.statusValue === 'evicted') return
    this.pendingUpdates.push(update)
    this.updateFlushTimer ??= setTimeout(() => {
      this.updateFlushTimer = null
      this.flushUpdates()
    }, this.options.updateFlushMs ?? 80)
  }

  private readonly handleAwarenessUpdate = (
    changes: { added: number[]; updated: number[]; removed: number[] },
    origin: unknown,
  ): void => {
    // Only locally-originated changes go out; `this` marks remote applies.
    if (origin === this || this.disposed || this.statusValue !== 'collaborating') return
    for (const clientId of [...changes.added, ...changes.updated, ...changes.removed]) {
      this.dirtyAwarenessClients.add(clientId)
    }
    // Trailing-edge throttle (~12/s at the 80ms default): the first change
    // arms the timer, every further change within the window coalesces
    // into the same frame — never a message per selection-change event.
    this.awarenessFlushTimer ??= setTimeout(() => {
      this.awarenessFlushTimer = null
      this.flushAwareness(false)
    }, this.options.awarenessFlushMs ?? 80)
  }

  private flushUpdates(): void {
    if (this.pendingUpdates.length === 0) return
    const merged = Y.mergeUpdates(this.pendingUpdates)
    this.pendingUpdates = []
    this.sendUpdate(merged)
  }

  private sendUpdate(update: Uint8Array): void {
    if (update.byteLength > UPDATE_MAX_BYTES) {
      // The server would drop it silently (telemetry counts bytes only);
      // dropping it knowingly with a local signal is the honest version.
      // A single >512 KiB Yjs transaction means a colossal paste — the
      // content is still in OUR doc and reaches the server via save.
      this.options.onOversizedUpdate?.()
      return
    }
    this.options.transport.pushUpdate(this.options.pageId, update).catch(() => {
      // Connection dropped mid-push. Do not retry-queue: the reconnect
      // path pushes full doc state, which supersedes any lost frame.
    })
  }

  private async pushFullState(): Promise<void> {
    try {
      await this.options.transport.pushUpdate(this.options.pageId, Y.encodeStateAsUpdate(this.doc))
    } catch {
      // As above — reconnect re-pushes.
    }
  }

  private flushAwareness(includeLocalRemoval: boolean): void {
    const clients = includeLocalRemoval
      ? [...new Set([...this.dirtyAwarenessClients, this.doc.clientID])]
      : [...this.dirtyAwarenessClients]
    this.dirtyAwarenessClients.clear()
    if (clients.length === 0) return
    // encodeAwarenessUpdate correctly encodes removed clients (null
    // state), so departures ride the same frame shape as caret moves.
    this.options.transport.pushAwareness(this.options.pageId, encodeAwarenessUpdate(this.awareness, clients))
  }

  private handleSeederLost(): void {
    // Only fires pre-seed (the server re-designates a seeder only while
    // the log is still empty): we were a joiner waiting for a seed that
    // will never come — produce it ourselves.
    if (this.seeded) return
    this.options.seed(this.doc, this)
    this.seeded = true
    void this.pushFullState()
  }

  private handleEvicted(): void {
    // canEdit revoked mid-session (design.md §6.7 rule-change sweep). No
    // retry-join: the doc stays intact locally (the user can still copy
    // their text) but nothing flows in either direction any more.
    this.teardown()
    removeAwarenessStates(
      this.awareness,
      [...this.awareness.getStates().keys()].filter((id) => id !== this.doc.clientID),
      this,
    )
    this.setStatus('evicted')
  }

  private async rejoinAfterReconnect(): Promise<void> {
    const { transport, pageId } = this.options
    let result: EditSessionJoin | null
    try {
      result = await transport.joinEditSession(pageId)
    } catch {
      // Reconnected transport immediately failed again; the next
      // onReconnected will retry. Status stays 'collaborating' — the doc
      // is intact and the user keeps editing locally.
      return
    }
    if (this.disposed) {
      if (result) void transport.leaveEditSession(pageId)
      return
    }

    if (result === null) {
      // Refused on rejoin — canEdit is gone (or the page is). Same
      // terminal read-only state as an explicit eviction, and same
      // no-retry-loop rule.
      this.handleEvicted()
      return
    }

    this.baseRevision = result.baseRevisionNumber
    this.options.onBaseRevisionChange?.(result.baseRevisionNumber)

    if (result.role === 'seeder') {
      // The session was dropped and we're the new seeder: seed from our
      // own live doc — a superset of saved content that preserves every
      // unsaved live edit we held at disconnect (see class doc).
      this.seeded = true
      await this.pushFullState()
    } else {
      // Replay into the existing doc: idempotent for shared history (the
      // usual case); see the class doc for the honest corner case. Then
      // push our full state so peers/log regain anything we did offline.
      for (const update of result.updateLog) {
        Y.applyUpdate(this.doc, update, this)
      }
      if (result.updateLog.length > 0) this.seeded = true
      await this.pushFullState()
    }

    // Re-announce our awareness state — the server holds no awareness, so
    // a rejoined connection is caret-invisible until it speaks again.
    if (this.awareness.getLocalState() !== null) {
      this.options.transport.pushAwareness(
        this.options.pageId,
        encodeAwarenessUpdate(this.awareness, [this.doc.clientID]),
      )
    }
  }

  private joinWithTimeout(timeoutMs: number): Promise<EditSessionJoin | null> {
    const join = this.options.transport.joinEditSession(this.options.pageId)
    let timer: ReturnType<typeof setTimeout> | null = null
    const timeout = new Promise<never>((_, reject) => {
      timer = setTimeout(() => reject(new Error('join timed out')), timeoutMs)
    })
    return Promise.race([join, timeout]).finally(() => {
      if (timer) clearTimeout(timer)
      // If the join eventually succeeds after we already fell back to
      // solo, withdraw — a ghost membership would hold the session open
      // and mis-credit contributors.
      void join
        .then((late) => {
          if (late && this.statusValue === 'solo') void this.options.transport.leaveEditSession(this.options.pageId)
        })
        .catch(() => {})
    })
  }

  private setStatus(status: CoEditStatus): void {
    if (this.statusValue === status) return
    this.statusValue = status
    this.options.onStatusChange?.(status)
  }
}
