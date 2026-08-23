import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { context, trace } from '@opentelemetry/api'
import {
  InMemorySpanExporter,
  SimpleSpanProcessor,
  StackContextManager,
  WebTracerProvider,
} from '@opentelemetry/sdk-trace-web'
import { createClient, fetchExchange, gql } from 'urql'
import { graphqlTracingExchange } from '../graphqlTracingExchange'

const PAGE_BY_ID = gql`
  query PageById($id: ID!, $search: String) {
    page(id: $id, search: $search) {
      id
      body
    }
  }
`

const RENAME_PAGE = gql`
  mutation RenamePage($id: ID!, $title: String!) {
    renamePage(id: $id, title: $title) {
      id
    }
  }
`

const ANONYMOUS = gql`
  {
    currentUser {
      id
    }
  }
`

const exporter = new InMemorySpanExporter()
const provider = new WebTracerProvider({ spanProcessors: [new SimpleSpanProcessor(exporter)] })

/** A `fetch` stand-in returning a well-formed GraphQL response, as urql's fetch source expects. */
function jsonFetch(body: unknown, onCall?: () => void): typeof fetch {
  return vi.fn(() => {
    onCall?.()
    return Promise.resolve(
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
  })
}

function clientWith(fetchImpl: typeof fetch) {
  return createClient({
    url: 'http://api.test/graphql',
    // The tracing exchange must sit before `fetchExchange`, as it does in
    // `graphql/client.ts`.
    exchanges: [graphqlTracingExchange(), fetchExchange],
    fetch: fetchImpl,
  })
}

beforeAll(() => {
  provider.register({ contextManager: new StackContextManager() })
})

afterEach(() => {
  exporter.reset()
})

afterAll(async () => {
  await provider.shutdown()
  trace.disable()
  context.disable()
})

describe('graphqlTracingExchange — what it records', () => {
  it('names the span "<type> <operation>" and records both as attributes', async () => {
    const client = clientWith(jsonFetch({ data: { page: { id: '42', body: 'x' } } }))
    await client.query(PAGE_BY_ID, { id: '42' }).toPromise()

    const [span] = exporter.getFinishedSpans()
    expect(span.name).toBe('query PageById')
    expect(span.attributes['graphql.operation.name']).toBe('PageById')
    expect(span.attributes['graphql.operation.type']).toBe('query')
  })

  it('records mutations the same way', async () => {
    const client = clientWith(jsonFetch({ data: { renamePage: { id: '42' } } }))
    await client.mutation(RENAME_PAGE, { id: '42', title: 'New title' }).toPromise()

    const [span] = exporter.getFinishedSpans()
    expect(span.name).toBe('mutation RenamePage')
    expect(span.attributes['graphql.operation.type']).toBe('mutation')
  })

  it('falls back to the bare operation type for an anonymous document', async () => {
    const client = clientWith(jsonFetch({ data: { currentUser: { id: 'u1' } } }))
    await client.query(ANONYMOUS, {}).toPromise()

    const [span] = exporter.getFinishedSpans()
    expect(span.name).toBe('query')
    expect(span.attributes).not.toHaveProperty('graphql.operation.name')
  })
})

describe('graphqlTracingExchange — what it must never record (design.md §15)', () => {
  it('carries no variable values, no document text, and no search query text', async () => {
    const client = clientWith(jsonFetch({ data: { page: { id: '42', body: 'secret body text' } } }))
    await client
      .query(PAGE_BY_ID, { id: 'page-7', search: 'classified propellant formula' })
      .toPromise()

    const [span] = exporter.getFinishedSpans()
    const serialised = JSON.stringify({ name: span.name, attributes: span.attributes, events: span.events })

    expect(serialised).not.toContain('classified')
    expect(serialised).not.toContain('propellant')
    expect(serialised).not.toContain('page-7')
    expect(serialised).not.toContain('secret body text')
    // The document itself is content-shaped too: field names describe the model.
    expect(serialised).not.toContain('$id: ID!')
    expect(span.attributes).not.toHaveProperty('graphql.document')
    expect(span.attributes).not.toHaveProperty('graphql.variables')
  })

  it('records only the error name on failure, never the message', async () => {
    const failing = vi.fn(() => Promise.reject(new TypeError('Failed to fetch http://api.test/graphql?q=classified')))
    const client = clientWith(failing as unknown as typeof fetch)
    await client.query(PAGE_BY_ID, { id: '42' }).toPromise()

    const [span] = exporter.getFinishedSpans()
    expect(span.status.message).toBe('TypeError')
    expect(JSON.stringify({ name: span.name, attributes: span.attributes, status: span.status })).not.toContain(
      'classified',
    )
  })
})

describe('graphqlTracingExchange — span lifecycle', () => {
  it('makes the GraphQL span active while the request is in flight, so the HTTP span nests under it', async () => {
    // This is the whole reason the exchange injects a fetch wrapper rather than
    // just reading operation names: the fetch instrumentation creates its span
    // synchronously inside this call and picks up whatever context is active.
    let activeDuringFetch: string | undefined
    const client = clientWith(
      jsonFetch({ data: { page: { id: '42', body: 'x' } } }, () => {
        activeDuringFetch = trace.getActiveSpan()?.spanContext().spanId
      }),
    )
    await client.query(PAGE_BY_ID, { id: '42' }).toPromise()

    const [span] = exporter.getFinishedSpans()
    expect(activeDuringFetch).toBeDefined()
    expect(activeDuringFetch).toBe(span.spanContext().spanId)
  })

  it('leaves no span active once the request has settled', async () => {
    const client = clientWith(jsonFetch({ data: { page: { id: '42', body: 'x' } } }))
    await client.query(PAGE_BY_ID, { id: '42' }).toPromise()
    expect(trace.getActiveSpan()).toBeUndefined()
  })

  it('ends the span when the request rejects — an aborted operation cannot leak one', async () => {
    // urql aborts in-flight fetches on teardown (component unmount), which
    // rejects the promise; the span is ended in a `finally`, so there is
    // nothing retained anywhere waiting for a result that never comes.
    const aborted = vi.fn(() => Promise.reject(new DOMException('Aborted', 'AbortError')))
    const client = clientWith(aborted as unknown as typeof fetch)
    await client.query(PAGE_BY_ID, { id: '42' }).toPromise()

    const spans = exporter.getFinishedSpans()
    expect(spans).toHaveLength(1)
    expect(spans[0].ended).toBe(true)
    expect(spans[0].status.message).toBe('AbortError')
  })

  it('delegates to a fetch implementation already present on the operation context', async () => {
    const underlying = jsonFetch({ data: { page: { id: '42', body: 'x' } } })
    const client = clientWith(underlying)
    await client.query(PAGE_BY_ID, { id: '42' }).toPromise()
    expect(underlying).toHaveBeenCalledOnce()
  })

  it('produces one span per network request, and none for a cache hit', async () => {
    const fetchImpl = jsonFetch({ data: { page: { id: '42', body: 'x' } } })
    const client = createClient({
      url: 'http://api.test/graphql',
      exchanges: [graphqlTracingExchange(), fetchExchange],
      fetch: fetchImpl,
    })
    await client.query(PAGE_BY_ID, { id: '42' }).toPromise()
    await client.query(PAGE_BY_ID, { id: '42' }).toPromise()

    // No cacheExchange in this pipeline, so both reach the network — the point
    // is one span per request, not one per operation dispatched.
    expect(exporter.getFinishedSpans()).toHaveLength(2)
    expect(fetchImpl).toHaveBeenCalledTimes(2)
  })
})
