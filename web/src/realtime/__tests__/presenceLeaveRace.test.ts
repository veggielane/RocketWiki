import { describe, expect, it, vi } from 'vitest'

/**
 * §1.7 — `LeavePage` was dropped when you navigated before the hub finished
 * connecting.
 *
 * `joinPage` awaits `ensureStarted()`; `usePresence` fires it un-awaited. On a
 * fast navigation the old code ran:
 *
 *   1. mount → `joinPage('A')` suspends inside `ensureStarted()`
 *   2. navigate → cleanup → `leavePage('A')` sees state `Connecting` and
 *      returns WITHOUT SENDING ANYTHING
 *   3. negotiation completes → the pending `joinPage('A')` resumes and invokes
 *      `JoinPage('A')`
 *
 * The client is then in page A's hub group with no matching leave, having left
 * the screen. Local handlers are unsubscribed so it is invisible on this side,
 * while the server still lists them as a viewer of a page they are not on.
 *
 * `FakePresenceTransport` has no connection state, so `usePresence.test.ts`
 * cannot reach this. The fake below has exactly the state machine that matters.
 */

// Stands in for the real `signalr` module: a connection whose `start()` the
// test controls, and which records every invoke in order.
const hub = vi.hoisted(() => {
  const state = { current: 'Disconnected' as 'Disconnected' | 'Connecting' | 'Connected' }
  const invokes: string[] = []
  let finishStart!: () => void
  const startGate = new Promise<void>((resolve) => {
    finishStart = resolve
  })

  const connection = {
    get state() {
      return state.current
    },
    start: vi.fn(async () => {
      state.current = 'Connecting'
      await startGate
      state.current = 'Connected'
    }),
    invoke: vi.fn(async (method: string, ...args: unknown[]) => {
      invokes.push(`${method}(${String(args[0])})`)
      return null
    }),
    on: vi.fn(),
    off: vi.fn(),
    onreconnected: vi.fn(),
    onreconnecting: vi.fn(),
    onclose: vi.fn(),
  }

  return { state, invokes, finishStart: () => finishStart(), connection }
})

vi.mock('@microsoft/signalr', () => ({
  HubConnectionState: { Disconnected: 'Disconnected', Connecting: 'Connecting', Connected: 'Connected' },
  HubConnectionBuilder: class {
    withUrl() {
      return this
    }
    withHubProtocol() {
      return this
    }
    withAutomaticReconnect() {
      return this
    }
    build() {
      return hub.connection
    }
  },
}))
vi.mock('@microsoft/signalr-protocol-msgpack', () => ({ MessagePackHubProtocol: class {} }))
vi.mock('../../graphql/authToken', () => ({ getAccessToken: () => 'token' }))

const { SignalRPresenceTransport } = await import('../SignalRPresenceTransport')

describe('connection loss is observable', () => {
  it('registers the loss handlers, not just the recovery one', () => {
    // `.withAutomaticReconnect()` was configured and only `onreconnected` was
    // ever wired, so the app could see recovery but never loss — which is what
    // let the "Live co-editing" chip stay green through a disconnect.
    new SignalRPresenceTransport('/hubs/presence')
    expect(hub.connection.onreconnecting).toHaveBeenCalled()
    expect(hub.connection.onclose).toHaveBeenCalled()
  })

  it('publishes the state each SignalR callback means', () => {
    const transport = new SignalRPresenceTransport('/hubs/presence')
    const seen: string[] = []
    transport.onConnectionStateChanged((state) => seen.push(state))

    // Fire the handlers SignalR would fire.
    const call = (fn: { mock: { calls: unknown[][] } }) => {
      const handler = fn.mock.calls.at(-1)?.[0]
      if (typeof handler !== 'function') throw new Error('handler was never registered')
      ;(handler as () => void)()
    }
    call(hub.connection.onreconnecting as never)
    call(hub.connection.onclose as never)
    call(hub.connection.onreconnected as never)

    expect(seen).toEqual(['reconnecting', 'disconnected', 'connected'])
  })
})

describe('presence group membership survives a navigation mid-connect', () => {
  it('sends LeavePage after the pending JoinPage, rather than dropping it', async () => {
    const transport = new SignalRPresenceTransport('/hubs/presence')

    // Mount: join starts and suspends inside `start()`.
    const joining = transport.joinPage('page-a')
    // Navigate away before the connection is up — exactly what usePresence's
    // cleanup does, and the moment the old guard bailed out on.
    const leaving = transport.leavePage('page-a')

    // The queue hands work to a microtask, so let the join actually reach
    // `start()` before asserting on the state it is suspended in.
    await Promise.resolve()
    await Promise.resolve()
    expect(hub.state.current).toBe('Connecting')

    // Negotiation completes.
    hub.finishStart()
    await joining
    await leaving

    // Both sent, and in the only order that leaves the server's view correct.
    expect(hub.invokes).toEqual(['JoinPage(page-a)', 'LeavePage(page-a)'])
  })
})
