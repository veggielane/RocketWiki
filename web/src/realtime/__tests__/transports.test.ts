import { describe, expect, it, vi, beforeEach } from 'vitest'
import {
  createNotificationsTransport,
  createPresenceTransport,
  getDefaultCoEditTransport,
  getDefaultNotificationsTransport,
  getDefaultPresenceTransport,
  notificationsHubUrl,
  isFakeRealtimeEnabled,
} from '../transports'
import { FakeNotificationsTransport } from '../FakeNotificationsTransport'
import { FakePresenceTransport } from '../FakePresenceTransport'
import { SignalRNotificationsTransport } from '../SignalRNotificationsTransport'
import { SignalRPresenceTransport } from '../SignalRPresenceTransport'
import { setAccessToken } from '../../graphql/authToken'

// The SignalR client is mocked at the module boundary: these tests cover
// the wiring — which implementation the seam picks, which URL the hub
// connection is built against, and how the bearer token is plumbed — never
// a live socket (vitest/jsdom has no hub to negotiate with).
const { withUrlCalls, builtConnections } = vi.hoisted(() => ({
  withUrlCalls: [] as { url: string; options: { accessTokenFactory?: () => string } }[],
  builtConnections: [] as { invoke: ReturnType<typeof vi.fn> }[],
}))

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    withUrl(url: string, options: { accessTokenFactory?: () => string }) {
      withUrlCalls.push({ url, options })
      return this
    }
    withAutomaticReconnect() {
      return this
    }
    withHubProtocol() {
      return this
    }
    build() {
      const connection = {
        start: vi.fn(),
        stop: vi.fn(),
        on: vi.fn(),
        off: vi.fn(),
        onreconnected: vi.fn(),
        // The loss half of the reconnect story — registered since the app
        // began reporting a dropped connection instead of asserting liveness.
        onreconnecting: vi.fn(),
        onclose: vi.fn(),
        invoke: vi.fn().mockResolvedValue(undefined),
        state: 'Disconnected',
      }
      builtConnections.push(connection)
      return connection
    }
  }
  return { HubConnectionBuilder, HubConnectionState: { Connected: 'Connected' } }
})

vi.mock('@microsoft/signalr-protocol-msgpack', () => ({ MessagePackHubProtocol: class {} }))

beforeEach(() => {
  withUrlCalls.length = 0
  builtConnections.length = 0
  setAccessToken(undefined)
})

describe('notificationsHubUrl', () => {
  it('defaults to the same-origin hub path', () => {
    expect(notificationsHubUrl({})).toBe('/hubs/notifications')
  })

  it('reads VITE_SIGNALR_NOTIFICATIONS_URL — the same variable the telemetry propagation allowlist derives from', () => {
    expect(notificationsHubUrl({ VITE_SIGNALR_NOTIFICATIONS_URL: 'https://api.example.test/hubs/notifications' })).toBe(
      'https://api.example.test/hubs/notifications',
    )
  })

  it('treats a blank value as unset rather than building a connection to an empty URL', () => {
    expect(notificationsHubUrl({ VITE_SIGNALR_NOTIFICATIONS_URL: '   ' })).toBe('/hubs/notifications')
  })
})

describe('transport selection seam', () => {
  it('the built app default (no flag) is the real SignalR transports', () => {
    expect(createNotificationsTransport({})).toBeInstanceOf(SignalRNotificationsTransport)
    expect(createPresenceTransport({})).toBeInstanceOf(SignalRPresenceTransport)
  })

  it('VITE_FAKE_REALTIME=true opts into the fakes (dev-without-backend)', () => {
    const env = { VITE_FAKE_REALTIME: 'true' }
    expect(createNotificationsTransport(env)).toBeInstanceOf(FakeNotificationsTransport)
    expect(createPresenceTransport(env)).toBeInstanceOf(FakePresenceTransport)
  })

  it('any value other than true means real — a typo must not silently disconnect the app from the hub', () => {
    expect(isFakeRealtimeEnabled({ VITE_FAKE_REALTIME: 'yes' })).toBe(false)
    expect(isFakeRealtimeEnabled({})).toBe(false)
  })

  it('both real transports connect to the one configured hub URL (notifications and presence share the hub)', () => {
    const env = { VITE_SIGNALR_NOTIFICATIONS_URL: 'https://api.example.test/hubs/notifications' }
    createNotificationsTransport(env)
    createPresenceTransport(env)
    expect(withUrlCalls.map((c) => c.url)).toEqual([
      'https://api.example.test/hubs/notifications',
      'https://api.example.test/hubs/notifications',
    ])
  })

  it('returns the same app-lifetime singleton on every call', () => {
    expect(getDefaultNotificationsTransport()).toBe(getDefaultNotificationsTransport())
    expect(getDefaultPresenceTransport()).toBe(getDefaultPresenceTransport())
  })

  it('co-editing rides the presence connection — getDefaultCoEditTransport IS the presence singleton (never a second hub connection)', () => {
    expect(getDefaultCoEditTransport()).toBe(getDefaultPresenceTransport())
  })
})

describe('JoinEditSession result normalization', () => {
  it('accepts the MessagePack wire shape (contractless resolver = PascalCase keys, number[] byte arrays) and the JSON one alike', async () => {
    const transport = new SignalRPresenceTransport('/hubs/notifications')
    const connection = builtConnections.at(-1)!

    // MessagePack's default contractless resolver serializes .NET property
    // names verbatim — this connection's live shape.
    connection.invoke.mockResolvedValueOnce({ Role: 'seeder', BaseRevisionNumber: 7, UpdateLog: [[1, 2, 3]] })
    const pascal = await transport.joinEditSession('page-1')
    expect(pascal).toEqual({ role: 'seeder', baseRevisionNumber: 7, updateLog: [Uint8Array.from([1, 2, 3])] })

    connection.invoke.mockResolvedValueOnce({ role: 'joiner', baseRevisionNumber: 3, updateLog: [] })
    const camel = await transport.joinEditSession('page-1')
    expect(camel).toEqual({ role: 'joiner', baseRevisionNumber: 3, updateLog: [] })
  })

  it('a null result (refused — indistinguishable from nonexistent, design.md §6.7) stays null', async () => {
    const transport = new SignalRPresenceTransport('/hubs/notifications')
    const connection = builtConnections.at(-1)!
    connection.invoke.mockResolvedValueOnce(null)
    expect(await transport.joinEditSession('page-1')).toBeNull()
  })
})

describe('bearer token plumbing (design.md §11: in-memory only)', () => {
  it('reads the current in-memory token fresh per invocation — a refresh mid-connection is picked up on reconnect', () => {
    createNotificationsTransport({})
    const factory = withUrlCalls[0].options.accessTokenFactory
    expect(factory).toBeDefined()

    setAccessToken('token-1')
    expect(factory!()).toBe('token-1')

    setAccessToken('token-2')
    expect(factory!()).toBe('token-2')
  })

  it('yields an empty string, not undefined, when signed out (the SignalR client requires a string)', () => {
    createPresenceTransport({})
    const factory = withUrlCalls[0].options.accessTokenFactory
    setAccessToken(undefined)
    expect(factory!()).toBe('')
  })
})
