import * as signalR from '@microsoft/signalr'
import { getAccessToken } from '../graphql/authToken'
import type { NotificationPayload, NotificationsTransport } from './types'

/**
 * Real implementation of `NotificationsTransport` against
 * `/hubs/notifications` (design.md §8) — the hub exists now (milestone 4b)
 * and adopted this event name (`Notification`) and payload shape verbatim
 * (NotificationsHub.cs / INotificationDispatcher.cs). Same bearer identity
 * as every other channel (design.md §11): `accessTokenFactory` reads the
 * in-memory token fresh per (re)connect, and the SignalR browser client
 * sends it as the `access_token` query string for WebSockets (a browser
 * cannot set an Authorization header on a WebSocket handshake) — which the
 * API accepts on `/hubs` paths only.
 */
export class SignalRNotificationsTransport implements NotificationsTransport {
  private readonly connection: signalR.HubConnection

  /**
   * Serializes start/stop. `useNotifications` connects in an effect and
   * disconnects in its cleanup, so React StrictMode's deliberate
   * mount/unmount/mount produces connect → disconnect → connect with the first
   * negotiate still in flight. Calling `stop()` on a connection that is still
   * starting, then `start()` on one that is still stopping, is not a benign
   * ordering: it threw two unhandled rejections ("The connection was stopped
   * during negotiation", then "Cannot start a HubConnection that is not in the
   * 'Disconnected' state") and left the hub socket permanently closed for the
   * rest of the session, because `withAutomaticReconnect` only retries a
   * connection that succeeded at least once. Notifications and presence were
   * simply dead until a full page reload.
   *
   * Found by driving a real browser through a real login. It was invisible to
   * the test suite because the suite substitutes FakeNotificationsTransport,
   * which has no connection state to race.
   */
  private operation: Promise<void> = Promise.resolve()

  constructor(hubUrl: string) {
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, { accessTokenFactory: () => getAccessToken() ?? '' })
      .withAutomaticReconnect()
      .build()
  }

  /** Runs `work` after whatever is already queued, whether that settled or threw. */
  private enqueue(work: () => Promise<void>): Promise<void> {
    const next = this.operation.then(work, work)
    // The chain itself must never stay rejected, or one failure poisons every
    // later connect/disconnect. Callers still see their own operation's result.
    this.operation = next.catch(() => {})
    return next
  }

  async connect(): Promise<void> {
    return this.enqueue(async () => {
      if (this.connection.state === signalR.HubConnectionState.Disconnected) {
        await this.connection.start()
      }
    })
  }

  async disconnect(): Promise<void> {
    return this.enqueue(async () => {
      if (this.connection.state !== signalR.HubConnectionState.Disconnected) {
        await this.connection.stop()
      }
    })
  }

  onNotification(handler: (notification: NotificationPayload) => void): () => void {
    this.connection.on('Notification', handler)
    return () => this.connection.off('Notification', handler)
  }
}
