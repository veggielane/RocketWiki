import * as signalR from '@microsoft/signalr'
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack'
import { getAccessToken } from '../graphql/authToken'
import type { PointerPosition, PresenceTransport, PresenceViewer } from './types'

/**
 * Real implementation of `PresenceTransport`. Presence lives on the same
 * hub as notifications (`/hubs/notifications` — NotificationsHub.cs owns
 * `JoinPage`/`LeavePage`/`PointerMove` and broadcasts `ViewersChanged`/
 * `PointerMoved`), which adopted this frontend's proposed method/event
 * names verbatim. Uses MessagePack (registered server-side via
 * `AddMessagePackProtocol`) rather than the default JSON protocol, since
 * this is the highest-frequency channel in the app. Token plumbing is the
 * same as SignalRNotificationsTransport: in-memory token via
 * `accessTokenFactory`, sent as the `access_token` query string on `/hubs`.
 */
export class SignalRPresenceTransport implements PresenceTransport {
  private readonly connection: signalR.HubConnection
  private started: Promise<void> | null = null

  constructor(hubUrl: string) {
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, { accessTokenFactory: () => getAccessToken() ?? '' })
      .withHubProtocol(new MessagePackHubProtocol())
      .withAutomaticReconnect()
      .build()
  }

  private async ensureStarted(): Promise<void> {
    this.started ??= this.connection.start()
    await this.started
  }

  async joinPage(pageId: string): Promise<void> {
    await this.ensureStarted()
    await this.connection.invoke('JoinPage', pageId)
  }

  async leavePage(pageId: string): Promise<void> {
    // No ensureStarted here: leaving must never throw because the
    // connection never finished starting — the caller's teardown path is
    // the last place we want an unhandled rejection.
    if (this.connection.state !== signalR.HubConnectionState.Connected) return
    await this.connection.invoke('LeavePage', pageId)
  }

  onViewersChanged(handler: (viewers: PresenceViewer[]) => void): () => void {
    this.connection.on('ViewersChanged', handler)
    return () => this.connection.off('ViewersChanged', handler)
  }

  onPointerMoved(handler: (position: PointerPosition) => void): () => void {
    this.connection.on('PointerMoved', handler)
    return () => this.connection.off('PointerMoved', handler)
  }

  sendPointerPosition(pageId: string, x: number, y: number): void {
    // Fire-and-forget: a dropped pointer sample is invisible (the next one
    // arrives in under 50ms), so this deliberately doesn't await or queue
    // — awaiting per-call would let a slow connection back up a queue of
    // increasingly-stale positions. `catch` swallows the rejection a
    // not-yet-connected invoke produces for the same reason.
    this.connection.invoke('PointerMove', pageId, x, y).catch(() => {})
  }
}
