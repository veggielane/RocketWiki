import { SpanKind, SpanStatusCode, context, trace } from '@opentelemetry/api'
import { getOperationName, makeOperation, mapExchange } from 'urql'
import type { Exchange } from 'urql'

/**
 * An urql exchange that wraps each GraphQL request in a span carrying the
 * operation's **name and type — never its variables, never its document**
 * (design.md §15). Operation names are static identifiers from
 * `graphql/operations/*.graphql`; variables are page ids, search text and
 * user attributes, which is exactly what §15 keeps out of telemetry.
 *
 * How it attaches to the HTTP span: urql resolves the fetch implementation as
 * `operation.context.fetch || fetch`, so this exchange injects a wrapper into
 * the operation context and calls the real fetch inside `context.with(...)`.
 * The fetch instrumentation creates its span synchronously inside that call
 * and therefore nests under this one. That is deliberate rather than
 * incidental — the browser context manager is `StackContextManager`, which
 * only propagates context synchronously, so anything relying on the span
 * still being active after an `await` would silently produce orphans.
 *
 * The span covers the **transport**, not the operation's outcome. A GraphQL
 * error arrives as HTTP 200 with an `errors` array in the body, so this span
 * is `OK` for a request whose result was an error — detecting otherwise would
 * mean reading the response body, which §15 forbids. Failed GraphQL operations
 * are visible on the server span, where the API's own instrumentation records
 * them.
 *
 * When no tracer provider is registered (telemetry not configured) every call
 * here hits the OpenTelemetry API's no-op implementation: a non-recording span
 * and a pass-through `context.with`. No network calls, no allocations beyond a
 * shared singleton.
 */

const TRACER_NAME = 'rocketwiki-web/graphql'

// Spelled out rather than imported from `@opentelemetry/semantic-conventions`:
// the GraphQL attributes live in that package's `/incubating` entrypoint, whose
// contents are explicitly unstable. Two string constants are cheaper than a
// dependency on names that may be renamed.
const ATTR_GRAPHQL_OPERATION_NAME = 'graphql.operation.name'
const ATTR_GRAPHQL_OPERATION_TYPE = 'graphql.operation.type'

type FetchImplementation = typeof fetch

/**
 * Error *names* only (`AbortError`, `TypeError`), never messages. A fetch
 * rejection message is browser-authored and can embed the request URL —
 * including the query string the redactor exists to remove.
 *
 * Duck-typed rather than `instanceof Error` on purpose: an aborted request
 * rejects with a `DOMException`, which is not an `Error` subclass in every
 * runtime, and `AbortError` is the single most common failure here — urql
 * aborts in-flight operations on every component unmount.
 */
function errorName(error: unknown): string {
  if (typeof error === 'object' && error !== null && 'name' in error) {
    const name = (error as { name: unknown }).name
    if (typeof name === 'string' && name.length > 0) return name
  }
  return 'Error'
}

function tracedFetch(
  delegate: FetchImplementation | undefined,
  operationType: string,
  operationName: string | undefined,
): FetchImplementation {
  return async (input, init) => {
    const span = trace.getTracer(TRACER_NAME).startSpan(
      // OTel's GraphQL convention: "<type> <name>", e.g. "query PageById".
      operationName ? `${operationType} ${operationName}` : operationType,
      {
        kind: SpanKind.CLIENT,
        attributes: {
          [ATTR_GRAPHQL_OPERATION_TYPE]: operationType,
          ...(operationName ? { [ATTR_GRAPHQL_OPERATION_NAME]: operationName } : {}),
        },
      },
    )

    try {
      return await context.with(trace.setSpan(context.active(), span), () =>
        // Resolved at call time, not captured: telemetry starts asynchronously
        // (the SDK is loaded on demand), so an early request must still pick up
        // the instrumented `globalThis.fetch` once it has been patched.
        delegate ? delegate(input, init) : globalThis.fetch(input, init),
      )
    } catch (error) {
      span.setStatus({ code: SpanStatusCode.ERROR, message: errorName(error) })
      throw error
    } finally {
      // Always ends, including on an aborted request — urql tears down
      // in-flight operations when a component unmounts, and an abort rejects
      // the fetch promise. There is no span held in a map anywhere, so a
      // cancelled operation cannot leak one.
      span.end()
    }
  }
}

export function graphqlTracingExchange(): Exchange {
  return mapExchange({
    onOperation(operation) {
      // `mapExchange` already forwards teardowns without calling us; guarding
      // anyway keeps this correct if it is ever moved to a hand-written exchange.
      if (operation.kind === 'teardown') return

      return makeOperation(operation.kind, operation, {
        ...operation.context,
        fetch: tracedFetch(operation.context.fetch, operation.kind, getOperationName(operation.query)),
      })
    },
  })
}
