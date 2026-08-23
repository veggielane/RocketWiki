import type { Attributes } from '@opentelemetry/api'
import type { ReadableSpan } from '@opentelemetry/sdk-trace-web'

/**
 * design.md §15: telemetry carries "no page content, no search query text, and
 * no attribute values; page and space identifiers only where needed to
 * diagnose". In a browser the whole risk is concentrated in URLs, because the
 * SPA puts real content into two places the OTel web instrumentations record
 * verbatim as `url.full`:
 *
 *   - **the query string** — `AppShell` navigates to `/search?q=<user's text>`,
 *     so the search box's contents end up in `location.href`, and from there on
 *     the `documentLoad`/`documentFetch` spans of anyone who lands on a search
 *     URL directly (bookmark, refresh, shared link).
 *   - **the fragment** — heading anchors are slugs derived from *heading text*
 *     (`editor/headingAnchors.ts`), so `/pages/42#thrust-vector-margins` is
 *     page content spelled out in a URL.
 *
 * Path segments are fine: page and space ids are exactly what §15 permits.
 *
 * So the rule is: **strip query and fragment from every URL-shaped attribute,
 * everywhere, unconditionally.** Nothing here tries to detect "sensitive"
 * URLs — a redactor that has to recognise the search route is one route away
 * from being wrong.
 *
 * This runs as an exporter decorator (`RedactingSpanExporter`) rather than as
 * per-instrumentation config, so it is a single choke point every span passes
 * through on its way out of the browser regardless of which instrumentation
 * produced it, including instrumentations added later by someone who never
 * read this file.
 */

/** Attributes whose value is a URL and must keep only scheme/host/path. */
const URL_VALUED_ATTRIBUTES = [
  'url.full', // fetch, XHR, documentLoad, documentFetch, resourceFetch
  'http.url', // pre-1.0 semconv spelling, in case an instrumentation lags
  'http.target', // pre-1.0 semconv: path *including* query
]

/**
 * Attributes that are nothing *but* the parts we refuse to export, plus the
 * content-bearing attributes a GraphQL/DB instrumentation would set. None of
 * these are produced by the instrumentations we register today; they are here
 * so that adding one later fails safe instead of quietly exporting documents.
 */
const FORBIDDEN_ATTRIBUTES = [
  'url.query',
  'url.fragment',
  'http.request.body',
  'http.response.body',
  'graphql.document',
  'graphql.variables',
  'db.statement',
  'db.query.text',
]

/** Matches a value that looks like an absolute or root-relative URL carrying a query/fragment. */
const URL_LIKE_WITH_QUERY_OR_FRAGMENT = /^(?:https?:\/\/|\/)[^\s]*[?#]/i

/**
 * Removes the query string and fragment, preserving everything else exactly —
 * including whether the input was absolute or relative.
 *
 * Deliberately string-based rather than `new URL()`: `URL` normalises (adds a
 * trailing slash to bare origins, percent-encodes, resolves `..`), and a
 * redactor that rewrites URLs it was only asked to truncate makes span data
 * harder to match against server-side spans for no benefit. Truncating at the
 * first `?` or `#` — whichever comes first — is also correct for values that
 * are not valid URLs at all, which `URL` would simply throw on.
 */
export function stripQueryAndFragment(value: string): string {
  const cut = value.search(/[?#]/)
  return cut === -1 ? value : value.slice(0, cut)
}

/**
 * Redacts one attribute bag in place. Mutation is intentional: `ReadableSpan`
 * exposes `attributes` as a readonly *property* holding a mutable object, and
 * rebuilding the span would drop its prototype (`spanContext()` is a method,
 * not an own property).
 */
export function redactAttributes(attributes: Attributes): void {
  for (const key of FORBIDDEN_ATTRIBUTES) {
    if (key in attributes) delete attributes[key]
  }

  for (const key of URL_VALUED_ATTRIBUTES) {
    const value = attributes[key]
    if (typeof value === 'string') {
      attributes[key] = stripQueryAndFragment(value)
    }
  }

  // Backstop for attribute keys nobody listed above. Only touches values that
  // are unambiguously URLs *and* actually carry a query or fragment, so it
  // cannot mangle ordinary strings.
  for (const [key, value] of Object.entries(attributes)) {
    if (typeof value === 'string' && URL_LIKE_WITH_QUERY_OR_FRAGMENT.test(value)) {
      attributes[key] = stripQueryAndFragment(value)
    }
  }
}

/** Redacts a span's own attributes and those of every event it carries. */
export function redactSpan(span: ReadableSpan): void {
  redactAttributes(span.attributes)
  for (const event of span.events) {
    if (event.attributes) redactAttributes(event.attributes)
  }
}
