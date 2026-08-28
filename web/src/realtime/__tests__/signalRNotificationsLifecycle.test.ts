import { describe, expect, it, vi, beforeEach } from 'vitest'
import { SignalRNotificationsTransport } from '../SignalRNotificationsTransport'

/**
 * The connect/disconnect ordering `useNotifications` actually produces.
 *
 * These exist because a real browser login exposed what the rest of the suite
 * structurally cannot: every component test substitutes
 * FakeNotificationsTransport, which has no connection state, so nothing
 * exercised what happens when React StrictMode's mount/unmount/mount fires
 * connect → disconnect → connect with a negotiate still in flight. Against a
 * live hub that closed the socket permanently for the session, since
 * `withAutomaticReconnect` only retries a connection that succeeded once.
 *
 * The fake below is deliberately stateful and asynchronous — a `start`/`stop`
 * pair that resolves immediately, or that ignores its own state, cannot fail
 * these tests and would make them decoration.
 */
const { connections } = vi.hoisted(() => ({
  connections: [] as {
    state: string
    start: () => Promise<void>
    stop: () => Promise<void>
    starts: number
    stops: number
  }[],
}))

vi.mock('@microsoft/signalr', () => {
  const tick = () => new Promise((resolve) => setTimeout(resolve, 1))
  class HubConnectionBuilder {
    withUrl() {
      return this
    }
    withAutomaticReconnect() {
      return this
    }
    build() {
      const connection = {
        state: 'Disconnected',
        starts: 0,
        stops: 0,
        async start() {
          if (this.state !== 'Disconnected') {
            throw new Error("Cannot start a HubConnection that is not in the 'Disconnected' state.")
          }
          this.state = 'Connecting'
          this.starts += 1
          await tick()
          // Mirrors the real client: a stop() landing mid-negotiate aborts it.
          if (this.state !== 'Connecting') {
            throw new Error('The connection was stopped during negotiation.')
          }
          this.state = 'Connected'
        },
        async stop() {
          this.state = 'Disconnecting'
          this.stops += 1
          await tick()
          this.state = 'Disconnected'
        },
        on: vi.fn(),
        off: vi.fn(),
      }
      connections.push(connection)
      return connection
    }
  }
  return {
    HubConnectionBuilder,
    HubConnectionState: { Disconnected: 'Disconnected', Connected: 'Connected' },
  }
})

beforeEach(() => {
  connections.length = 0
})

describe('SignalRNotificationsTransport lifecycle', () => {
  it('ends connected after StrictMode connect/disconnect/connect', async () => {
    const transport = new SignalRNotificationsTransport('/hubs/notifications')
    const connection = connections[0]

    // Exactly what useNotifications does, without awaiting between calls —
    // the effect body and its cleanup both fire-and-forget.
    const first = transport.connect()
    const teardown = transport.disconnect()
    const second = transport.connect()
    await Promise.all([first, teardown, second])

    expect(connection.state).toBe('Connected')
  })

  it('does not reject on that sequence, because the caller fire-and-forgets', async () => {
    const transport = new SignalRNotificationsTransport('/hubs/notifications')

    const settled = await Promise.allSettled([
      transport.connect(),
      transport.disconnect(),
      transport.connect(),
    ])

    expect(settled.map((s) => s.status)).toEqual(['fulfilled', 'fulfilled', 'fulfilled'])
  })

  it('is idempotent: connecting twice does not start a second time', async () => {
    const transport = new SignalRNotificationsTransport('/hubs/notifications')
    const connection = connections[0]

    await Promise.all([transport.connect(), transport.connect()])

    expect(connection.starts).toBe(1)
    expect(connection.state).toBe('Connected')
  })

  it('stays usable after a failed connect rather than caching the rejection', async () => {
    const transport = new SignalRNotificationsTransport('/hubs/notifications')
    const connection = connections[0]
    const realStart = connection.start.bind(connection)
    let failNext = true
    connection.start = async function () {
      if (failNext) {
        failNext = false
        throw new Error('negotiate failed')
      }
      await realStart()
    }

    await expect(transport.connect()).rejects.toThrow('negotiate failed')
    await transport.connect()

    expect(connection.state).toBe('Connected')
  })
})
