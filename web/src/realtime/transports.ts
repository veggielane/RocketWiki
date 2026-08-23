import { FakeNotificationsTransport } from './FakeNotificationsTransport'
import { FakePresenceTransport } from './FakePresenceTransport'
import { SignalRNotificationsTransport } from './SignalRNotificationsTransport'
import { SignalRPresenceTransport } from './SignalRPresenceTransport'
import type { NotificationsTransport, PresenceTransport } from './types'

/**
 * The one seam that decides which realtime implementation the app gets.
 * The built app uses the real SignalR transports against
 * `/hubs/notifications` (one hub carries both notifications and presence —
 * NotificationsHub.cs); the fakes exist for tests and for running the SPA
 * with no backend, opted into explicitly via `VITE_FAKE_REALTIME=true`.
 * Unset means real: dev-without-backend is the special case that should
 * need the flag, never production.
 *
 * `env` is injectable the same way telemetry/config.ts does it, so the
 * selection logic is unit-testable without stubbing Vite globals.
 */
export type RealtimeEnv = Record<string, unknown>

/**
 * Same variable the telemetry propagation allowlist derives from
 * (telemetry/config.ts reads it into `apiUrls`) — one URL of truth, so a
 * deployment that moves the hub cross-origin can't end up with traceparent
 * propagation and the actual SignalR traffic disagreeing about where the
 * API lives.
 */
export function notificationsHubUrl(env: RealtimeEnv = import.meta.env): string {
  const value = env['VITE_SIGNALR_NOTIFICATIONS_URL']
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : '/hubs/notifications'
}

export function isFakeRealtimeEnabled(env: RealtimeEnv = import.meta.env): boolean {
  return env['VITE_FAKE_REALTIME'] === 'true' || env['VITE_FAKE_REALTIME'] === true
}

export function createNotificationsTransport(env: RealtimeEnv = import.meta.env): NotificationsTransport {
  return isFakeRealtimeEnabled(env)
    ? new FakeNotificationsTransport()
    : new SignalRNotificationsTransport(notificationsHubUrl(env))
}

export function createPresenceTransport(env: RealtimeEnv = import.meta.env): PresenceTransport {
  return isFakeRealtimeEnabled(env) ? new FakePresenceTransport() : new SignalRPresenceTransport(notificationsHubUrl(env))
}

/**
 * App-lifetime singletons, created lazily on first use rather than at
 * module load — component modules import these without a test that merely
 * imports them constructing hub connections. One instance per process:
 * recreating a transport per render/mount would connect/disconnect (or
 * join/leave) on every re-render.
 */
let defaultNotificationsTransport: NotificationsTransport | undefined
let defaultPresenceTransport: PresenceTransport | undefined

export function getDefaultNotificationsTransport(): NotificationsTransport {
  defaultNotificationsTransport ??= createNotificationsTransport()
  return defaultNotificationsTransport
}

export function getDefaultPresenceTransport(): PresenceTransport {
  defaultPresenceTransport ??= createPresenceTransport()
  return defaultPresenceTransport
}
