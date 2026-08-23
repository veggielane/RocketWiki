import { describe, expect, it, vi, beforeEach } from 'vitest'
import {
  createNotificationsTransport,
  createPresenceTransport,
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
const { withUrlCalls } = vi.hoisted(() => ({
  withUrlCalls: [] as { url: string; options: { accessTokenFactory?: () => string } }[],
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
      return {
        start: vi.fn(),
        stop: vi.fn(),
        on: vi.fn(),
        off: vi.fn(),
        invoke: vi.fn().mockResolvedValue(undefined),
        state: 'Disconnected',
      }
    }
  }
  return { HubConnectionBuilder, HubConnectionState: { Connected: 'Connected' } }
})

vi.mock('@microsoft/signalr-protocol-msgpack', () => ({ MessagePackHubProtocol: class {} }))

beforeEach(() => {
  withUrlCalls.length = 0
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
