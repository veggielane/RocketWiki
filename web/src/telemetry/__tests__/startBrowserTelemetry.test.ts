import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { context, propagation, trace } from '@opentelemetry/api'
import type { ReadableSpan } from '@opentelemetry/sdk-trace-web'
import { startBrowserTelemetry } from '../startBrowserTelemetry'
import type { TelemetryHandle } from '../tracing'

/**
 * The app-level gate, exercised through the real SDK with only the OTLP
 * *transport* replaced.
 *
 * Replacing it is not squeamishness about the network. Under vitest's jsdom
 * environment, `@opentelemetry/exporter-trace-otlp-http` resolves to its Node
 * build and sends over `node:http` — the first draft of this file produced a
 * genuine `ECONNREFUSED ::1:18889`. That is the wrong transport to be
 * exercising (the browser build sends over `fetch`) and it is not our code
 * either way. What is ours, and what these tests cover, is everything up to
 * the exporter's front door: the on/off gate, the resolved endpoint, the
 * propagator, the instrumentations, and the redaction every span passes
 * through on the way out.
 */

const otlp = vi.hoisted(() => ({
  /** Spans handed to the OTLP exporter — i.e. post-redaction, as they would go on the wire. */
  exported: [] as ReadableSpan[],
  /** Constructor configs, to check what endpoint and headers the exporter was built with. */
  configs: [] as { url?: string; headers?: Record<string, string> }[],
}))

vi.mock('@opentelemetry/exporter-trace-otlp-http', () => ({
  OTLPTraceExporter: class {
    constructor(config: { url?: string; headers?: Record<string, string> }) {
      otlp.configs.push(config)
    }
    export(spans: ReadableSpan[], resultCallback: (result: { code: number }) => void) {
      otlp.exported.push(...spans)
      resultCallback({ code: 0 })
    }
    shutdown() {
      return Promise.resolve()
    }
    forceFlush() {
      return Promise.resolve()
    }
  },
}))

let started: TelemetryHandle | null = null
let fetchStub: ReturnType<typeof vi.fn>
const realFetch = globalThis.fetch

function isTracingActive(): boolean {
  const span = trace.getTracer('gate-test').startSpan('probe')
  const recording = span.isRecording()
  span.end()
  return recording
}

/** Records a span the way `DocumentLoadInstrumentation` would for a search URL. */
function recordSearchPageLoad(): void {
  const span = trace.getTracer('gate-test').startSpan('documentLoad')
  span.setAttribute('url.full', 'http://localhost:5173/search?q=classified+propellant')
  span.end()
}

async function flush(): Promise<void> {
  await started?.shutdown()
  started = null
}

beforeEach(() => {
  otlp.exported.length = 0
  otlp.configs.length = 0
  fetchStub = vi.fn(() => Promise.resolve(new Response('{}', { status: 200 })))
  globalThis.fetch = fetchStub as unknown as typeof fetch
})

afterEach(async () => {
  await flush()
  trace.disable()
  context.disable()
  propagation.disable()
  globalThis.fetch = realFetch
  vi.unstubAllEnvs()
})

describe('startBrowserTelemetry — disabled unless configured', () => {
  it('does nothing when no OTLP endpoint is configured', async () => {
    vi.stubEnv('VITE_OTEL_EXPORTER_OTLP_ENDPOINT', '')
    vi.stubEnv('VITE_OTEL_EXPORTER_OTLP_TRACES_ENDPOINT', '')

    expect(await startBrowserTelemetry()).toBeNull()
    expect(isTracingActive()).toBe(false)
  })

  it('builds no exporter and sends nothing when unconfigured', async () => {
    vi.stubEnv('VITE_OTEL_EXPORTER_OTLP_ENDPOINT', '')
    await startBrowserTelemetry()
    isTracingActive()

    expect(otlp.configs).toHaveLength(0)
    expect(otlp.exported).toHaveLength(0)
    expect(fetchStub).not.toHaveBeenCalled()
  })

  it('does nothing when the configured endpoint is unusable', async () => {
    vi.stubEnv('VITE_OTEL_EXPORTER_OTLP_ENDPOINT', 'not a url')

    expect(await startBrowserTelemetry()).toBeNull()
    expect(isTracingActive()).toBe(false)
    expect(otlp.configs).toHaveLength(0)
  })

  it('leaves globalThis.fetch unpatched when unconfigured', async () => {
    vi.stubEnv('VITE_OTEL_EXPORTER_OTLP_ENDPOINT', '')
    await startBrowserTelemetry()

    expect(globalThis.fetch).toBe(fetchStub)
  })
})

describe('startBrowserTelemetry — enabled', () => {
  beforeEach(() => {
    vi.stubEnv('VITE_OTEL_EXPORTER_OTLP_ENDPOINT', 'http://localhost:18889')
  })

  it('starts tracing when an OTLP endpoint is configured', async () => {
    started = await startBrowserTelemetry()

    expect(started).not.toBeNull()
    expect(isTracingActive()).toBe(true)
  })

  it('builds the exporter against the resolved traces endpoint', async () => {
    started = await startBrowserTelemetry()

    expect(otlp.configs).toHaveLength(1)
    expect(otlp.configs[0].url).toBe('http://localhost:18889/v1/traces')
  })

  it('passes configured headers through to the exporter', async () => {
    vi.stubEnv('VITE_OTEL_EXPORTER_OTLP_HEADERS', 'x-api-key=abc123')
    started = await startBrowserTelemetry()

    expect(otlp.configs[0].headers).toEqual({ 'x-api-key': 'abc123' })
  })

  it('installs the fetch instrumentation', async () => {
    started = await startBrowserTelemetry()

    expect(globalThis.fetch).not.toBe(fetchStub)
  })

  it('registers a traceparent-only propagator, never baggage', async () => {
    started = await startBrowserTelemetry()

    // W3C `baggage` is a general-purpose key/value channel onto outbound
    // requests — exactly the shape of an accidental second record of user
    // context (design.md §15). The SDK's default propagator includes it; ours
    // must not.
    expect(propagation.fields()).toEqual(['traceparent', 'tracestate'])
  })

  it('tags exported spans with the service resource attributes', async () => {
    vi.stubEnv('VITE_APP_VERSION', '1.4.0')
    started = await startBrowserTelemetry()
    isTracingActive()
    await flush()

    const resource = otlp.exported[0].resource.attributes
    expect(resource['service.name']).toBe('rocketwiki-web')
    expect(resource['service.version']).toBe('1.4.0')
  })

  it('redacts search text before the exporter ever sees the span', async () => {
    // The end of the chain: not "the redactor works" (redaction.test.ts covers
    // that) but "nothing in the assembled pipeline routes around it".
    started = await startBrowserTelemetry()
    recordSearchPageLoad()
    await flush()

    const attributes = otlp.exported.find((span) => span.name === 'documentLoad')?.attributes
    expect(attributes?.['url.full']).toBe('http://localhost:5173/search')
    expect(JSON.stringify(otlp.exported.map((span) => span.attributes))).not.toContain('classified')
  })
})
