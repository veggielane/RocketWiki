/**
 * Who is allowed to receive a `traceparent` header, and whose requests are not
 * traced at all.
 *
 * Trace context is a header we attach to *outgoing* requests, so the allowlist
 * is a boundary question, not a convenience one: a `traceparent` sent to a
 * third-party origin hands that origin a correlatable id for this session's
 * activity. RocketWiki only ever needs it on requests to its own API — that is
 * the whole point, joining the browser span to the server span — so the
 * allowlist is derived from the configured API endpoints and nothing else.
 *
 * The OTel web SDK already propagates unconditionally to the SPA's own origin
 * (`shouldPropagateTraceHeaders` short-circuits on an origin match), which
 * covers the normal deployment where nginx/the Vite proxy terminates
 * `/graphql`, `/attachments` and `/hubs` on the SPA's origin. These patterns
 * therefore only matter when the API is genuinely on another origin.
 */

/**
 * `urlMatches` in `@opentelemetry/core` treats a **string** pattern as exact
 * equality against the full URL, so an origin string would match nothing.
 * Patterns must be regexes; hence the escaping here.
 */
function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

/** Anchors a pattern to an origin so `https://api.example.com` cannot match `https://api.example.com.evil.test`. */
function originPattern(origin: string): RegExp {
  return new RegExp(`^${escapeRegExp(origin)}(?:/|$)`)
}

/**
 * Cross-origin API origins that may receive `traceparent`.
 *
 * Same-origin entries are dropped rather than included: the SDK propagates to
 * them regardless, and listing them would suggest the list is the complete
 * story about propagation when it isn't.
 *
 * Note for whoever deploys a cross-origin API: the server must also list
 * `traceparent` in `Access-Control-Allow-Headers`, or the browser will fail the
 * preflight and the request itself — propagation is not free cross-origin.
 */
export function crossOriginApiPatterns(apiUrls: string[], currentOrigin: string): RegExp[] {
  const origins = new Set<string>()

  for (const apiUrl of apiUrls) {
    let resolved: URL
    try {
      resolved = new URL(apiUrl, currentOrigin)
    } catch {
      continue // Unparseable config: propagate to nothing rather than guess.
    }
    if (resolved.origin === currentOrigin) continue
    origins.add(resolved.origin)
  }

  return [...origins].map(originPattern)
}

/**
 * Matches the OTLP exporter's own endpoint so the fetch/XHR instrumentations
 * skip it.
 *
 * Without this, exporting a batch of spans issues a request, which is traced,
 * which produces a span, which is exported — a self-sustaining loop that grows
 * on its own traffic. Matched by origin *and* path prefix rather than origin
 * alone, so a collector reverse-proxied onto the app's own origin (e.g.
 * `/otlp/v1/traces`) doesn't silently disable tracing for the whole app.
 */
export function exporterIgnorePattern(tracesEndpoint: string): RegExp {
  const parsed = new URL(tracesEndpoint)
  return new RegExp(`^${escapeRegExp(parsed.origin + parsed.pathname)}`)
}
