import * as signalR from '@microsoft/signalr'
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack'
import { getAccessToken } from '../graphql/authToken'
import type {
  CoEditTransport,
  EditSessionJoin,
  PresenceTransport,
  PresenceViewer,
  RealtimeConnectionState,
  ReseedReason,
} from './types'

/**
 * Real implementation of `PresenceTransport` — and of `CoEditTransport`,
 * because both live on the same hub connection. Presence lives on the same
 * hub as notifications (`/hubs/notifications` — NotificationsHub.cs owns
 * `JoinRoom`/`LeaveRoom` and broadcasts `ViewersChanged`), which adopted
 * this frontend's proposed method/event names verbatim. Uses MessagePack
 * (registered server-side via
 * `AddMessagePackProtocol`) rather than the default JSON protocol, since
 * this is the highest-frequency channel in the app. Token plumbing is the
 * same as SignalRNotificationsTransport: in-memory token via
 * `accessTokenFactory`, sent as the `access_token` query string on `/hubs`.
 *
 * Co-editing (design.md §8, NotificationsHub.EditSessions.cs) rides this
 * exact connection rather than opening a second one — one client, one hub
 * connection — and MessagePack is what makes the `byte[]` protocol
 * arguments arrive as real `Uint8Array`s instead of base64 strings (the
 * JSON hub protocol would stringify them). `normalizeBytes` below is a
 * defensive belt for exactly that: if a deployment ever swaps the protocol,
 * updates still decode instead of silently corrupting.
 */
export class SignalRPresenceTransport implements PresenceTransport, CoEditTransport {
  private readonly connection: signalR.HubConnection
  private started: Promise<void> | null = null
  private readonly reconnectedHandlers = new Set<() => void>()

  constructor(hubUrl: string) {
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, { accessTokenFactory: () => getAccessToken() ?? '' })
      .withHubProtocol(new MessagePackHubProtocol())
      .withAutomaticReconnect()
      .build()
    // SignalR's `onreconnected` has no `off` — fan out through our own set
    // so subscribers (one provider per edit session) can unsubscribe.
    this.connection.onreconnected(() => {
      for (const handler of this.reconnectedHandlers) handler()
      this.emitConnectionState('connected')
    })
    // The two halves that were never registered. Without them the app could
    // observe recovery but never loss, so every realtime surface went on
    // asserting it was live throughout a disconnect.
    this.connection.onreconnecting(() => this.emitConnectionState('reconnecting'))
    this.connection.onclose(() => this.emitConnectionState('disconnected'))
  }

  private readonly connectionStateHandlers = new Set<(state: RealtimeConnectionState) => void>()

  private emitConnectionState(state: RealtimeConnectionState): void {
    for (const handler of this.connectionStateHandlers) handler(state)
  }

  onConnectionStateChanged(handler: (state: RealtimeConnectionState) => void): () => void {
    this.connectionStateHandlers.add(handler)
    return () => this.connectionStateHandlers.delete(handler)
  }

  private async ensureStarted(): Promise<void> {
    this.started ??= this.connection.start()
    await this.started
  }

  /**
   * Serialises group membership so a join and its matching leave cannot
   * overtake each other. Ported from SignalRNotificationsTransport, which
   * solved this class of problem first.
   *
   * The bug this closes: `leaveRoom` used to return early whenever the
   * connection was not yet `Connected`. On a fast navigation the join suspends
   * inside `ensureStarted()`, the cleanup's leave sees `Connecting` and sends
   * NOTHING, and then negotiation completes and the pending join runs — leaving
   * the client in the hub group for a page it has navigated away from, with no
   * leave to follow. Local handlers are gone so it is invisible here, while the
   * server still lists them as a viewer of a page they are not on. That is the
   * "live data leak, not just a resource leak" this module's own types warn
   * about.
   *
   * Queuing rather than state-checking is what makes it correct: the leave now
   * waits for the join to finish and then actually sends.
   */
  private membership: Promise<void> = Promise.resolve()

  /** Runs `work` after whatever is already queued, whether that settled or threw. */
  private enqueue(work: () => Promise<void>): Promise<void> {
    const next = this.membership.then(work, work)
    // The chain must never stay rejected, or one failure poisons every later
    // join/leave. Callers still see their own operation's result.
    this.membership = next.catch(() => {})
    return next
  }

  async joinRoom(roomKey: string): Promise<void> {
    return this.enqueue(async () => {
      await this.ensureStarted()
      await this.connection.invoke('JoinRoom', roomKey)
    })
  }

  async leaveRoom(roomKey: string): Promise<void> {
    return this.enqueue(async () => {
      // Still no `ensureStarted`: if nothing ever started there is no group to
      // leave, and a teardown path is the last place to raise. But this now
      // runs AFTER any queued join, so the state check reflects the join's
      // outcome rather than racing it.
      if (this.connection.state !== signalR.HubConnectionState.Connected) return
      await this.connection.invoke('LeaveRoom', roomKey)
    })
  }

  onViewersChanged(handler: (viewers: PresenceViewer[]) => void): () => void {
    this.connection.on('ViewersChanged', handler)
    return () => this.connection.off('ViewersChanged', handler)
  }

  // ---- CoEditTransport (same connection, design.md §8 co-editing) ----

  async joinEditSession(pageId: string): Promise<EditSessionJoin | null> {
    // Deliberately NOT queued: this one returns a value the caller needs, and
    // the queue's purpose is ordering membership against its own teardown —
    // which `leaveEditSession` achieves by waiting for whatever is in flight.
    await this.ensureStarted()
    const result = await this.connection.invoke<Record<string, unknown> | null>('JoinEditSession', pageId)
    if (result == null) return null
    // Property-name casing differs by hub protocol: the JSON protocol
    // camelCases .NET properties, but MessagePack's default contractless
    // resolver serializes them VERBATIM (PascalCase). This connection runs
    // MessagePack, so PascalCase is the live shape — both are accepted so
    // a protocol swap can't silently null every field.
    const role = result['role'] ?? result['Role']
    const baseRevisionNumber = result['baseRevisionNumber'] ?? result['BaseRevisionNumber']
    const updateLog = result['updateLog'] ?? result['UpdateLog']
    return {
      role: role === 'seeder' ? 'seeder' : 'joiner',
      baseRevisionNumber: Number(baseRevisionNumber),
      updateLog: (Array.isArray(updateLog) ? updateLog : []).map(normalizeBytes),
    }
  }

  async leaveEditSession(pageId: string): Promise<void> {
    // Same queue as the page group, and for the same reason: an edit session
    // left behind is a membership the server still believes in.
    return this.enqueue(async () => {
      if (this.connection.state !== signalR.HubConnectionState.Connected) return
      await this.connection.invoke('LeaveEditSession', pageId)
    })
  }

  pushUpdate(pageId: string, update: Uint8Array): Promise<void> {
    // NOT fire-and-forget, unlike awareness below: a dropped Yjs update is
    // missing content on every peer, not a stale caret. The provider
    // awaits/handles the rejection (and its reconnect path re-pushes full
    // state, which is what actually closes any gap a drop opened).
    return this.connection.invoke('PushUpdate', pageId, update)
  }

  pushAwareness(pageId: string, awarenessUpdate: Uint8Array): void {
    // Fire-and-forget: the next caret sample supersedes this one within a
    // tick, so a dropped frame is invisible, and awaiting per-call would let
    // a slow connection back up a queue of increasingly-stale carets. `catch`
    // swallows the rejection a not-yet-connected invoke produces for the same
    // reason.
    this.connection.invoke('PushAwareness', pageId, awarenessUpdate).catch(() => {})
  }

  reseedEditSession(pageId: string, fullState: Uint8Array): Promise<void> {
    return this.connection.invoke('ReseedEditSession', pageId, fullState)
  }

  onUpdateReceived(handler: (pageId: string, update: Uint8Array) => void): () => void {
    const wrapped = (pageId: string, update: unknown) => handler(pageId, normalizeBytes(update))
    this.connection.on('UpdateReceived', wrapped)
    return () => this.connection.off('UpdateReceived', wrapped)
  }

  onAwarenessReceived(handler: (pageId: string, awarenessUpdate: Uint8Array) => void): () => void {
    const wrapped = (pageId: string, update: unknown) => handler(pageId, normalizeBytes(update))
    this.connection.on('AwarenessReceived', wrapped)
    return () => this.connection.off('AwarenessReceived', wrapped)
  }

  onReseedRequired(handler: (pageId: string, baseRevisionNumber: number, reason: ReseedReason) => void): () => void {
    const wrapped = (pageId: string, baseRevisionNumber: number, reason: string) =>
      handler(pageId, baseRevisionNumber, reason === 'log_cap' ? 'log_cap' : 'seeder_lost')
    this.connection.on('ReseedRequired', wrapped)
    return () => this.connection.off('ReseedRequired', wrapped)
  }

  onEvictedFromEditSession(handler: (pageId: string) => void): () => void {
    this.connection.on('EvictedFromEditSession', handler)
    return () => this.connection.off('EvictedFromEditSession', handler)
  }

  onReconnected(handler: () => void): () => void {
    this.reconnectedHandlers.add(handler)
    return () => this.reconnectedHandlers.delete(handler)
  }
}

/**
 * MessagePack delivers hub `byte[]`s as `Uint8Array` already; the JSON hub
 * protocol would deliver base64 strings, and MessagePack sometimes hands
 * back plain number arrays depending on encoder settings. Normalizing here
 * keeps the provider's contract honest (`Uint8Array` in, `Uint8Array` out)
 * whatever the wire protocol did.
 */
function normalizeBytes(value: unknown): Uint8Array {
  if (value instanceof Uint8Array) return value
  if (value instanceof ArrayBuffer) return new Uint8Array(value)
  if (Array.isArray(value)) return Uint8Array.from(value as number[])
  if (typeof value === 'string') {
    const binary = atob(value)
    const bytes = new Uint8Array(binary.length)
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
    return bytes
  }
  throw new Error('Unrecognized binary payload from the hub')
}
