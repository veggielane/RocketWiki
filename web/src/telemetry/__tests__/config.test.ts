import { describe, expect, it } from 'vitest'
import { DEFAULT_SERVICE_NAME, parseHeaders, readTelemetryConfig } from '../config'

describe('readTelemetryConfig — the off-by-default gate', () => {
  it('returns null when no OTLP endpoint is configured', () => {
    expect(readTelemetryConfig({})).toBeNull()
  })

  it('returns null when the endpoint is present but empty or whitespace', () => {
    expect(readTelemetryConfig({ VITE_OTEL_EXPORTER_OTLP_ENDPOINT: '' })).toBeNull()
    expect(readTelemetryConfig({ VITE_OTEL_EXPORTER_OTLP_ENDPOINT: '   ' })).toBeNull()
  })

  it('stays off when the endpoint is set to a non-string value', () => {
    expect(readTelemetryConfig({ VITE_OTEL_EXPORTER_OTLP_ENDPOINT: true })).toBeNull()
  })

  it('stays off rather than half-on when the endpoint is unparseable', () => {
    expect(readTelemetryConfig({ VITE_OTEL_EXPORTER_OTLP_ENDPOINT: 'not a url' })).toBeNull()
    expect(readTelemetryConfig({ VITE_OTEL_EXPORTER_OTLP_ENDPOINT: 'localhost:18889' })).toBeNull()
  })

  it('refuses non-http schemes — telemetry leaves over OTLP/HTTP or not at all', () => {
    expect(readTelemetryConfig({ VITE_OTEL_EXPORTER_OTLP_ENDPOINT: 'grpc://collector:4317' })).toBeNull()
    expect(readTelemetryConfig({ VITE_OTEL_EXPORTER_OTLP_ENDPOINT: 'file:///tmp/traces' })).toBeNull()
  })

  it('appends /v1/traces to a base endpoint', () => {
    const config = readTelemetryConfig({ VITE_OTEL_EXPORTER_OTLP_ENDPOINT: 'http://localhost:18889' })
    expect(config?.tracesEndpoint).toBe('http://localhost:18889/v1/traces')
  })

  it('does not double up the slash when the base endpoint has a trailing one', () => {
    const config = readTelemetryConfig({ VITE_OTEL_EXPORTER_OTLP_ENDPOINT: 'http://localhost:18889//' })
    expect(config?.tracesEndpoint).toBe('http://localhost:18889/v1/traces')
  })

  it('uses the signal-specific endpoint verbatim, in preference to the base', () => {
    const config = readTelemetryConfig({
      VITE_OTEL_EXPORTER_OTLP_ENDPOINT: 'http://base:18889',
      VITE_OTEL_EXPORTER_OTLP_TRACES_ENDPOINT: 'https://collector.internal/otlp/traces',
    })
    expect(config?.tracesEndpoint).toBe('https://collector.internal/otlp/traces')
  })
})

describe('readTelemetryConfig — resource attributes', () => {
  const enabled = { VITE_OTEL_EXPORTER_OTLP_ENDPOINT: 'http://localhost:18889' }

  it('defaults the service name to rocketwiki-web', () => {
    expect(readTelemetryConfig(enabled)?.serviceName).toBe(DEFAULT_SERVICE_NAME)
    expect(DEFAULT_SERVICE_NAME).toBe('rocketwiki-web')
  })

  it('allows the service name to be overridden', () => {
    const config = readTelemetryConfig({ ...enabled, VITE_OTEL_SERVICE_NAME: 'rocketwiki-web-low' })
    expect(config?.serviceName).toBe('rocketwiki-web-low')
  })

  it('omits service.version entirely when no app version is configured', () => {
    const config = readTelemetryConfig(enabled)
    expect(config).not.toHaveProperty('serviceVersion')
  })

  it('carries the app version through when one is configured', () => {
    const config = readTelemetryConfig({ ...enabled, VITE_APP_VERSION: '1.4.0+abc1234' })
    expect(config?.serviceVersion).toBe('1.4.0+abc1234')
  })
})

describe('readTelemetryConfig — API endpoints for the propagation allowlist', () => {
  const enabled = { VITE_OTEL_EXPORTER_OTLP_ENDPOINT: 'http://localhost:18889' }

  it('falls back to the same-origin defaults the app itself uses', () => {
    expect(readTelemetryConfig(enabled)?.apiUrls).toEqual(['/graphql', '/hubs/notifications'])
  })

  it('picks up the app’s own endpoint vars rather than a telemetry-specific copy', () => {
    const config = readTelemetryConfig({
      ...enabled,
      VITE_GRAPHQL_URL: 'https://api.rocketwiki.internal/graphql',
      VITE_SIGNALR_NOTIFICATIONS_URL: 'https://api.rocketwiki.internal/hubs/notifications',
    })
    expect(config?.apiUrls).toEqual([
      'https://api.rocketwiki.internal/graphql',
      'https://api.rocketwiki.internal/hubs/notifications',
    ])
  })
})

describe('parseHeaders', () => {
  it('is empty when unset', () => {
    expect(parseHeaders(undefined)).toEqual({})
  })

  it('parses the OTLP key=value,key=value encoding', () => {
    expect(parseHeaders('x-api-key=abc123,x-tenant=aero')).toEqual({
      'x-api-key': 'abc123',
      'x-tenant': 'aero',
    })
  })

  it('trims surrounding whitespace on both sides of a pair', () => {
    expect(parseHeaders(' x-api-key = abc123 , x-tenant = aero ')).toEqual({
      'x-api-key': 'abc123',
      'x-tenant': 'aero',
    })
  })

  it('keeps `=` characters inside a value', () => {
    expect(parseHeaders('authorization=Basic dXNlcjpwYXNz==')).toEqual({
      authorization: 'Basic dXNlcjpwYXNz==',
    })
  })

  it('skips malformed pairs instead of throwing', () => {
    expect(parseHeaders('novalue,=orphan,,good=yes')).toEqual({ good: 'yes' })
  })
})
