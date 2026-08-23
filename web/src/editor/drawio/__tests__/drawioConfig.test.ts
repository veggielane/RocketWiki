import { describe, expect, it } from 'vitest'
import { buildEmbedUrl, embedOrigin, readDrawioConfig } from '../drawioConfig'

/**
 * The config gate (design.md §15, telemetry's fail-closed precedent): no
 * VITE_DRAWIO_URL, no editor — never a silent default to the public
 * internet service.
 */
describe('readDrawioConfig', () => {
  it('unset means null — no default editor URL exists', () => {
    expect(readDrawioConfig({})).toBeNull()
  })

  it('empty / whitespace-only means null', () => {
    expect(readDrawioConfig({ VITE_DRAWIO_URL: '' })).toBeNull()
    expect(readDrawioConfig({ VITE_DRAWIO_URL: '   ' })).toBeNull()
  })

  it('an unparseable URL disables the editor rather than being handed to an iframe', () => {
    expect(readDrawioConfig({ VITE_DRAWIO_URL: 'not a url' })).toBeNull()
  })

  it('non-http(s) schemes are refused', () => {
    expect(readDrawioConfig({ VITE_DRAWIO_URL: 'ftp://drawio.internal' })).toBeNull()
    expect(readDrawioConfig({ VITE_DRAWIO_URL: 'javascript:alert(1)' })).toBeNull()
  })

  it('a self-hosted in-network instance is accepted and not flagged as the public service', () => {
    const config = readDrawioConfig({ VITE_DRAWIO_URL: 'http://drawio.internal:8080' })
    expect(config).not.toBeNull()
    expect(config?.publicService).toBe(false)
  })

  it('the public diagrams.net / draw.io hosts are flagged so the dialog can warn', () => {
    expect(readDrawioConfig({ VITE_DRAWIO_URL: 'https://embed.diagrams.net' })?.publicService).toBe(true)
    expect(readDrawioConfig({ VITE_DRAWIO_URL: 'https://app.diagrams.net' })?.publicService).toBe(true)
    expect(readDrawioConfig({ VITE_DRAWIO_URL: 'https://www.draw.io' })?.publicService).toBe(true)
  })

  it('a lookalike host (diagrams.net.evil.example) is NOT treated as the public service — but is also not blocked; the flag is advisory', () => {
    const config = readDrawioConfig({ VITE_DRAWIO_URL: 'https://diagrams.net.evil.example' })
    expect(config?.publicService).toBe(false)
  })
})

describe('buildEmbedUrl / embedOrigin', () => {
  it('appends the JSON embed-protocol switches', () => {
    const config = readDrawioConfig({ VITE_DRAWIO_URL: 'http://drawio.internal:8080' })!
    const url = new URL(buildEmbedUrl(config))
    expect(url.searchParams.get('embed')).toBe('1')
    expect(url.searchParams.get('proto')).toBe('json')
    expect(url.searchParams.get('spin')).toBe('1')
    expect(url.searchParams.get('noSaveBtn')).toBe('1')
    expect(url.origin).toBe('http://drawio.internal:8080')
  })

  it('embedOrigin is the origin postMessage traffic is restricted to', () => {
    const config = readDrawioConfig({ VITE_DRAWIO_URL: 'https://drawio.internal/some/path' })!
    expect(embedOrigin(config)).toBe('https://drawio.internal')
  })
})
