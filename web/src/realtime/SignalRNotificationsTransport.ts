import * as signalR from '@microsoft/signalr'
import { getAccessToken } from '../graphql/authToken'
import type { NotificationPayload, NotificationsTransport } from './types'

/**
 * Real implementation of `NotificationsTransport` against
 * `/hubs/notifications` (design.md §8). The hub doesn't exist yet
 * (milestone 4b) — this has never been connected to a live server, so
 * treat the hub method/event name below (`Notification`) as this
 * frontend's proposal for the contract, not a confirmed one. Same bearer
 * token as every other channel (design.md §11), read fresh per (re)connect
 * rather than once, so a token refresh during a long-lived connection
 * doesn't leave it authenticating with a stale one.
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
