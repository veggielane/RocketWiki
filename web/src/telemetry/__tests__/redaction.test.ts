import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Attributes } from '@opentelemetry/api'
import { InMemorySpanExporter, SimpleSpanProcessor, WebTracerProvider } from '@opentelemetry/sdk-trace-web'
import { RedactingSpanExporter } from '../RedactingSpanExporter'
import { redactAttributes, stripQueryAndFragment } from '../redaction'

describe('stripQueryAndFragment', () => {
  it('leaves a URL with neither query nor fragment untouched', () => {
    expect(stripQueryAndFragment('https://wiki.internal/pages/42')).toBe('https://wiki.internal/pages/42')
  })

  it('removes a query string', () => {
    expect(stripQueryAndFragment('https://wiki.internal/search?q=secret')).toBe('https://wiki.internal/search')
  })

  it('removes a fragment', () => {
    expect(stripQueryAndFragment('https://wiki.internal/pages/42#thrust-vector-margins')).toBe(
      'https://wiki.internal/pages/42',
    )
  })

  it('removes both, cutting at whichever comes first', () => {
    expect(stripQueryAndFragment('https://wiki.internal/search?q=secret#hit-3')).toBe('https://wiki.internal/search')
    expect(stripQueryAndFragment('https://wiki.internal/p/42#anchor?notaquery')).toBe('https://wiki.internal/p/42')
  })

  it('keeps a relative URL relative and an absolute one absolute', () => {
    expect(stripQueryAndFragment('/search?q=secret')).toBe('/search')
    expect(stripQueryAndFragment('http://a.test/x?y=1')).toBe('http://a.test/x')
  })

  it('does not normalise the part it keeps', () => {
    // A redactor that rewrites URLs it was only asked to truncate makes browser
    // spans stop matching the server spans they should join.
    expect(stripQueryAndFragment('http://a.test?q=1')).toBe('http://a.test')
    expect(stripQueryAndFragment('/a/../b?q=1')).toBe('/a/../b')
  })

  it('truncates values that are not valid URLs at all rather than throwing', () => {
    expect(stripQueryAndFragment('garbage?q=secret')).toBe('garbage')
  })
})

describe('redactAttributes', () => {
  it('strips the query from every URL-valued attribute key the web instrumentations use', () => {
    const attributes: Attributes = {
      'url.full': 'http://localhost:5173/search?q=classified',
      'http.url': 'http://localhost:5173/search?q=classified',
      'http.target': '/search?q=classified',
    }
    redactAttributes(attributes)
    expect(attributes).toEqual({
      'url.full': 'http://localhost:5173/search',
      'http.url': 'http://localhost:5173/search',
      'http.target': '/search',
    })
  })

  it('drops attributes that are nothing but the forbidden parts', () => {
    const attributes: Attributes = {
      'url.query': 'q=classified',
      'url.fragment': 'thrust-vector-margins',
      'graphql.document': 'query PageById($id: ID!) { page(id: $id) { body } }',
      'graphql.variables': '{"id":"42"}',
      'db.statement': 'select * from pages',
      'db.query.text': 'select * from pages',
      'http.request.body': '{"query":"..."}',
      'http.response.body': '{"data":{"page":{"body":"..."}}}',
    }
    redactAttributes(attributes)
    expect(attributes).toEqual({})
  })

  it('keeps the identifiers §15 explicitly permits', () => {
    const attributes: Attributes = {
      'url.full': 'https://wiki.internal/spaces/AERO/pages/42',
      'http.request.method': 'POST',
      'http.response.status_code': 200,
      'graphql.operation.name': 'PageById',
    }
    redactAttributes(attributes)
    expect(attributes).toEqual({
      'url.full': 'https://wiki.internal/spaces/AERO/pages/42',
      'http.request.method': 'POST',
      'http.response.status_code': 200,
      'graphql.operation.name': 'PageById',
    })
  })

  it('catches URL-shaped values under attribute keys nobody listed', () => {
    // The backstop: an instrumentation added later that invents its own key.
    const attributes: Attributes = { 'some.future.instrumentation.url': '/search?q=classified' }
    redactAttributes(attributes)
    expect(attributes['some.future.instrumentation.url']).toBe('/search')
  })

  it('leaves ordinary strings containing punctuation alone', () => {
    const attributes: Attributes = {
      'error.type': 'TypeError',
      'some.note': 'is this a query? no',
      'some.tag': '#aerospace',
    }
    redactAttributes(attributes)
    expect(attributes).toEqual({
      'error.type': 'TypeError',
      'some.note': 'is this a query? no',
      'some.tag': '#aerospace',
    })
  })

  it('leaves non-string attribute values alone', () => {
    const attributes: Attributes = { 'http.response.status_code': 404, 'a.flag': true, 'a.list': ['x', 'y'] }
    redactAttributes(attributes)
    expect(attributes).toEqual({ 'http.response.status_code': 404, 'a.flag': true, 'a.list': ['x', 'y'] })
  })
})

describe('RedactingSpanExporter — the choke point every span passes through', () => {
  const memory = new InMemorySpanExporter()
  const provider = new WebTracerProvider({
    spanProcessors: [new SimpleSpanProcessor(new RedactingSpanExporter(memory))],
  })
  const tracer = provider.getTracer('redaction-test')

  afterEach(() => {
    memory.reset()
  })

  it('removes search text from a documentLoad-shaped span before it is exported', () => {
    // Exactly what `/search?q=...` produces: AppShell navigates there, so the
    // search box's contents reach `location.href` and then `url.full`.
    const span = tracer.startSpan('documentLoad')
    span.setAttribute('url.full', 'http://localhost:5173/search?q=classified+propellant+formula')
    span.end()

    const exported = memory.getFinishedSpans()
    expect(exported).toHaveLength(1)
    expect(exported[0].attributes['url.full']).toBe('http://localhost:5173/search')
    expect(JSON.stringify(exported[0].attributes)).not.toContain('classified')
  })

  it('removes heading-anchor fragments, which are slugs of page content', () => {
    const span = tracer.startSpan('documentLoad')
    span.setAttribute('url.full', 'http://localhost:5173/pages/42#stage-two-ignition-anomaly')
    span.end()

    expect(memory.getFinishedSpans()[0].attributes['url.full']).toBe('http://localhost:5173/pages/42')
  })

  it('redacts attributes on span events too, not just the span itself', () => {
    const span = tracer.startSpan('HTTP POST')
    span.addEvent('redirect', { 'url.full': '/search?q=classified' })
    span.end()

    const [exported] = memory.getFinishedSpans()
    expect(exported.events[0].attributes?.['url.full']).toBe('/search')
  })

  it('preserves page and space ids in the path — the identifiers §15 allows', () => {
    const span = tracer.startSpan('HTTP GET')
    span.setAttributes({
      'url.full': 'http://localhost:5173/attachments/9f2c?download=1',
      'http.request.method': 'GET',
      'http.response.status_code': 200,
    })
    span.end()

    const [exported] = memory.getFinishedSpans()
    expect(exported.attributes['url.full']).toBe('http://localhost:5173/attachments/9f2c')
    expect(exported.attributes['http.request.method']).toBe('GET')
    expect(exported.attributes['http.response.status_code']).toBe(200)
  })

  it('delegates shutdown and forceFlush to the wrapped exporter', async () => {
    const shutdown = vi.fn(() => Promise.resolve())
    const forceFlush = vi.fn(() => Promise.resolve())
    const exporter = new RedactingSpanExporter({ export: vi.fn(), shutdown, forceFlush })

    await exporter.forceFlush()
    await exporter.shutdown()

    expect(forceFlush).toHaveBeenCalledOnce()
    expect(shutdown).toHaveBeenCalledOnce()
  })

  it('resolves forceFlush even when the wrapped exporter does not implement it', async () => {
    const exporter = new RedactingSpanExporter({ export: vi.fn(), shutdown: () => Promise.resolve() })
    await expect(exporter.forceFlush()).resolves.toBeUndefined()
  })
})
