import type { ExportResult } from '@opentelemetry/core'
import type { ReadableSpan, SpanExporter } from '@opentelemetry/sdk-trace-web'
import { redactSpan } from './redaction'

/**
 * Wraps a `SpanExporter` so that every span is redacted (see `redaction.ts`)
 * immediately before it leaves the browser.
 *
 * The point of doing this at the exporter rather than per instrumentation is
 * that it is the *last* thing in the pipeline: there is exactly one place to
 * audit, and an instrumentation added later — or a `span.setAttribute` call
 * somewhere in the app — cannot route around it. design.md §15 is a rule about
 * what crosses the wire, so it is enforced at the wire.
 */
export class RedactingSpanExporter implements SpanExporter {
  private readonly delegate: SpanExporter

  constructor(delegate: SpanExporter) {
    this.delegate = delegate
  }

  export(spans: ReadableSpan[], resultCallback: (result: ExportResult) => void): void {
    for (const span of spans) {
      redactSpan(span)
    }
    this.delegate.export(spans, resultCallback)
  }

  shutdown(): Promise<void> {
    return this.delegate.shutdown()
  }

  forceFlush(): Promise<void> {
    return this.delegate.forceFlush?.() ?? Promise.resolve()
  }
}
