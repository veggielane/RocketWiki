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

  constructor(hubUrl: string) {
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, { accessTokenFactory: () => getAccessToken() ?? '' })
      .withAutomaticReconnect()
      .build()
  }

  async connect(): Promise<void> {
    await this.connection.start()
  }

  async disconnect(): Promise<void> {
    await this.connection.stop()
  }

  onNotification(handler: (notification: NotificationPayload) => void): () => void {
    this.connection.on('Notification', handler)
    return () => this.connection.off('Notification', handler)
  }
}
