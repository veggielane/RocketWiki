import { describe, expect, it } from 'vitest'
import { isUrlIgnored } from '@opentelemetry/core'
import { shouldPropagateTraceHeaders } from '@opentelemetry/sdk-trace-web'
import { crossOriginApiPatterns, exporterIgnorePattern } from '../propagation'

// These tests deliberately feed the produced patterns to the SDK's *own*
// matchers rather than asserting on regex source. What matters is the decision
// the SDK reaches — `urlMatches` treats a string pattern as exact equality, a
// trap that asserting on our own output would not catch.

const SPA_ORIGIN = 'https://wiki.internal'
const API_ORIGIN = 'https://api.rocketwiki.internal'

describe('crossOriginApiPatterns', () => {
  it('produces nothing when the API is same-origin (the normal deployment)', () => {
    // nginx and the Vite dev proxy both terminate /graphql and /hubs on the
    // SPA's own origin; the SDK propagates there unconditionally.
    expect(crossOriginApiPatterns(['/graphql', '/hubs/notifications'], SPA_ORIGIN)).toEqual([])
  })

  it('still propagates to a same-origin API even with an empty allowlist', () => {
    const patterns = crossOriginApiPatterns(['/graphql'], SPA_ORIGIN)
    expect(shouldPropagateTraceHeaders(`${window.location.origin}/graphql`, patterns)).toBe(true)
  })

  it('allows a cross-origin API host', () => {
    const patterns = crossOriginApiPatterns([`${API_ORIGIN}/graphql`], SPA_ORIGIN)
    expect(shouldPropagateTraceHeaders(`${API_ORIGIN}/graphql`, patterns)).toBe(true)
  })

  it('allows every path on an allowed API origin, not just the configured one', () => {
    // /attachments and /hubs are on the same API origin but are never listed.
    const patterns = crossOriginApiPatterns([`${API_ORIGIN}/graphql`], SPA_ORIGIN)
    expect(shouldPropagateTraceHeaders(`${API_ORIGIN}/attachments/9f2c`, patterns)).toBe(true)
  })

  it('does not leak traceparent to an unrelated third-party origin', () => {
    const patterns = crossOriginApiPatterns([`${API_ORIGIN}/graphql`], SPA_ORIGIN)
    expect(shouldPropagateTraceHeaders('https://analytics.example.com/collect', patterns)).toBe(false)
    expect(shouldPropagateTraceHeaders('https://cdn.example.com/font.woff2', patterns)).toBe(false)
  })

  it('anchors on the origin so a lookalike host does not match', () => {
    const patterns = crossOriginApiPatterns([`${API_ORIGIN}/graphql`], SPA_ORIGIN)
    expect(shouldPropagateTraceHeaders('https://api.rocketwiki.internal.evil.test/x', patterns)).toBe(false)
  })

  it('treats a different port on the same host as a different origin', () => {
    const patterns = crossOriginApiPatterns(['http://localhost:5001/graphql'], 'http://localhost:5173')
    expect(shouldPropagateTraceHeaders('http://localhost:5001/graphql', patterns)).toBe(true)
    expect(shouldPropagateTraceHeaders('http://localhost:9999/graphql', patterns)).toBe(false)
  })

  it('collapses several endpoints on one API origin into a single pattern', () => {
    const patterns = crossOriginApiPatterns(
      [`${API_ORIGIN}/graphql`, `${API_ORIGIN}/hubs/notifications`],
      SPA_ORIGIN,
    )
    expect(patterns).toHaveLength(1)
  })

  it('keeps distinct API origins separate', () => {
    const patterns = crossOriginApiPatterns(
      [`${API_ORIGIN}/graphql`, 'https://hubs.rocketwiki.internal/hubs/notifications'],
      SPA_ORIGIN,
    )
    expect(patterns).toHaveLength(2)
    expect(shouldPropagateTraceHeaders('https://hubs.rocketwiki.internal/hubs/notifications', patterns)).toBe(true)
  })

  it('propagates to nothing extra when an endpoint is unparseable', () => {
    const patterns = crossOriginApiPatterns(['http://[malformed', `${API_ORIGIN}/graphql`], SPA_ORIGIN)
    expect(patterns).toHaveLength(1)
    expect(shouldPropagateTraceHeaders(`${API_ORIGIN}/graphql`, patterns)).toBe(true)
  })
})

describe('exporterIgnorePattern — breaking the export feedback loop', () => {
  it('ignores the exporter’s own endpoint', () => {
    const ignoreUrls = [exporterIgnorePattern('http://localhost:18889/v1/traces')]
    expect(isUrlIgnored('http://localhost:18889/v1/traces', ignoreUrls)).toBe(true)
  })

  it('still ignores it when the exporter appends to the path or a query', () => {
    const ignoreUrls = [exporterIgnorePattern('http://localhost:18889/v1/traces')]
    expect(isUrlIgnored('http://localhost:18889/v1/traces?x=1', ignoreUrls)).toBe(true)
  })

  it('does not silence the whole app when the collector is proxied onto the app origin', () => {
    // A collector reverse-proxied at /otlp/v1/traces must not stop /graphql
    // from being traced — that is why this matches on path, not just origin.
    const ignoreUrls = [exporterIgnorePattern('https://wiki.internal/otlp/v1/traces')]
    expect(isUrlIgnored('https://wiki.internal/otlp/v1/traces', ignoreUrls)).toBe(true)
    expect(isUrlIgnored('https://wiki.internal/graphql', ignoreUrls)).toBe(false)
    expect(isUrlIgnored('https://wiki.internal/attachments/9f2c', ignoreUrls)).toBe(false)
  })

  it('does not ignore a different collector host', () => {
    const ignoreUrls = [exporterIgnorePattern('http://localhost:18889/v1/traces')]
    expect(isUrlIgnored('http://localhost:4318/v1/traces', ignoreUrls)).toBe(false)
  })
})
