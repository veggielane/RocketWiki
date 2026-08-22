import type { NotificationPayload, NotificationsTransport } from './types'

/**
 * Stands in for a real `/hubs/notifications` connection, which doesn't
 * exist yet (design.md §8, milestone 4b). `emit` is the test/dev hook for
 * simulating an incoming push — nothing here talks to a network.
 */
export class FakeNotificationsTransport implements NotificationsTransport {
  private handlers = new Set<(notification: NotificationPayload) => void>()
  connected = false

  async connect(): Promise<void> {
    this.connected = true
  }

  async disconnect(): Promise<void> {
    this.connected = false
    this.handlers.clear()
  }

  onNotification(handler: (notification: NotificationPayload) => void): () => void {
    this.handlers.add(handler)
    return () => this.handlers.delete(handler)
  }

  /** Test/dev only: simulates the hub pushing a notification. */
  emit(notification: NotificationPayload): void {
    for (const handler of this.handlers) {
      handler(notification)
    }
  }
}
