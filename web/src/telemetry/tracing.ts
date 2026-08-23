import { W3CTraceContextPropagator } from '@opentelemetry/core'
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http'
import { registerInstrumentations } from '@opentelemetry/instrumentation'
import { DocumentLoadInstrumentation } from '@opentelemetry/instrumentation-document-load'
import { FetchInstrumentation } from '@opentelemetry/instrumentation-fetch'
import { XMLHttpRequestInstrumentation } from '@opentelemetry/instrumentation-xml-http-request'
import { resourceFromAttributes } from '@opentelemetry/resources'
import { BatchSpanProcessor, StackContextManager, WebTracerProvider } from '@opentelemetry/sdk-trace-web'
import { ATTR_SERVICE_NAME, ATTR_SERVICE_VERSION } from '@opentelemetry/semantic-conventions'
import { RedactingSpanExporter } from './RedactingSpanExporter'
import type { TelemetryConfig } from './config'
import { crossOriginApiPatterns, exporterIgnorePattern } from './propagation'

/**
 * The actual tracing SDK wiring. This module is only ever reached through
 * `startBrowserTelemetry`'s dynamic `import()`, so in a build with no OTLP
 * endpoint configured it lands in a separate chunk that is never requested —
 * "zero overhead when unset" means zero bytes downloaded, not just no spans.
 *
 * Nothing here has run against a live OTLP endpoint. The pipeline is exercised
 * by tests with an in-memory exporter; the export leg itself is unproven, in
 * the same sense as the rest of §16's standing caveat.
 */

export interface TelemetryHandle {
  /** Unregisters instrumentations and flushes pending spans. Tests use it; the app does not need to. */
  shutdown(): Promise<void>
}

export function initTracing(config: TelemetryConfig): TelemetryHandle {
  const exporter = new RedactingSpanExporter(
    new OTLPTraceExporter({ url: config.tracesEndpoint, headers: config.headers }),
  )

  const provider = new WebTracerProvider({
    resource: resourceFromAttributes({
      [ATTR_SERVICE_NAME]: config.serviceName,
      ...(config.serviceVersion ? { [ATTR_SERVICE_VERSION]: config.serviceVersion } : {}),
    }),
    // Batched, not simple: a span-per-request exporter in a browser doubles the
    // request count of every page. The browser BatchSpanProcessor also flushes
    // on document hide by default, which is the only reliable "page is going
    // away" hook there is.
    spanProcessors: [new BatchSpanProcessor(exporter)],
  })

  provider.register({
    contextManager: new StackContextManager(),
    // `traceparent` only. The SDK's default is a composite that also injects
    // W3C `baggage`, which is a general-purpose key/value channel onto
    // outbound requests — precisely the kind of thing that turns into a second
    // record of user context by accident (design.md §15). We never set
    // baggage, so registering the propagator would only create the opening.
    propagator: new W3CTraceContextPropagator(),
  })

  const propagateTraceHeaderCorsUrls = crossOriginApiPatterns(config.apiUrls, window.location.origin)
  const ignoreUrls = [exporterIgnorePattern(config.tracesEndpoint)]

  const unregister = registerInstrumentations({
    tracerProvider: provider,
    instrumentations: [
      new DocumentLoadInstrumentation(),
      // Covers urql's GraphQL POSTs and the `/attachments` routes; the XHR
      // instrumentation additionally covers SignalR's negotiate/long-poll
      // fallbacks, which are XHR rather than fetch.
      new FetchInstrumentation({ propagateTraceHeaderCorsUrls, ignoreUrls, clearTimingResources: true }),
      new XMLHttpRequestInstrumentation({ propagateTraceHeaderCorsUrls, ignoreUrls, clearTimingResources: true }),
    ],
  })

  return {
    async shutdown() {
      unregister()
      await provider.shutdown()
    },
  }
}
