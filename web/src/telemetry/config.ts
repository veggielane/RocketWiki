/**
 * Browser telemetry configuration (design.md §15 "Telemetry is not audit").
 *
 * Telemetry is **off unless an OTLP endpoint is configured**. There is no
 * default endpoint and no "send to the current origin" fallback: an
 * unconfigured build makes zero telemetry network calls, and the tracing SDK
 * is never even downloaded (see `startBrowserTelemetry`, which `import()`s the
 * SDK only after this function returns a config).
 *
 * That default matters more here than in a typical SPA. §15 requires the
 * collector to stay inside the network boundary, so "someone forgot to
 * configure it" must fail closed — silently exporting nowhere — rather than
 * guessing at a destination.
 */

/** Environment shape this module reads. `import.meta.env` satisfies it. */
export type TelemetryEnv = Record<string, unknown>

export interface TelemetryConfig {
  /** Fully-resolved OTLP/HTTP traces URL, e.g. `http://localhost:18889/v1/traces`. */
  tracesEndpoint: string
  /** Extra headers for the exporter request (auth for the collector, usually empty in dev). */
  headers: Record<string, string>
  serviceName: string
  /** Omitted entirely when no app version is configured — an attribute is better absent than wrong. */
  serviceVersion?: string
  /**
   * API endpoints this SPA talks to, exactly as configured. `propagation.ts`
   * turns these into the `traceparent` allowlist; they are kept raw (possibly
   * relative) here so this module needs no `window`.
   */
  apiUrls: string[]
}

export const DEFAULT_SERVICE_NAME = 'rocketwiki-web'

function readString(env: TelemetryEnv, name: string): string | undefined {
  const value = env[name]
  if (typeof value !== 'string') return undefined
  const trimmed = value.trim()
  return trimmed.length > 0 ? trimmed : undefined
}

/**
 * `key=value,key2=value2`, the same encoding the OTel spec defines for
 * `OTEL_EXPORTER_OTLP_HEADERS`. Malformed pairs are skipped rather than
 * throwing — a bad header should not take telemetry (or the app) down.
 */
export function parseHeaders(raw: string | undefined): Record<string, string> {
  const headers: Record<string, string> = {}
  if (!raw) return headers
  for (const pair of raw.split(',')) {
    const separator = pair.indexOf('=')
    if (separator <= 0) continue
    const key = pair.slice(0, separator).trim()
    const value = pair.slice(separator + 1).trim()
    if (key.length === 0) continue
    headers[key] = value
  }
  return headers
}

/**
 * Resolves the traces URL from the two spec-shaped variables:
 *   - `VITE_OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` is signal-specific and used verbatim.
 *   - `VITE_OTEL_EXPORTER_OTLP_ENDPOINT` is a base, and `/v1/traces` is appended.
 *
 * Returns undefined when unset *or* unparseable. A typo'd endpoint disables
 * telemetry instead of handing the exporter a URL it will fail on for the
 * lifetime of the page.
 */
function resolveTracesEndpoint(env: TelemetryEnv): string | undefined {
  const explicit = readString(env, 'VITE_OTEL_EXPORTER_OTLP_TRACES_ENDPOINT')
  const base = readString(env, 'VITE_OTEL_EXPORTER_OTLP_ENDPOINT')
  const candidate = explicit ?? (base ? `${base.replace(/\/+$/, '')}/v1/traces` : undefined)
  if (!candidate) return undefined

  try {
    const parsed = new URL(candidate)
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return undefined
    return parsed.toString()
  } catch {
    return undefined
  }
}

/**
 * Returns null when telemetry is not configured — the caller must treat that
 * as "do nothing at all", not "use defaults".
 */
export function readTelemetryConfig(env: TelemetryEnv): TelemetryConfig | null {
  const tracesEndpoint = resolveTracesEndpoint(env)
  if (!tracesEndpoint) return null

  const serviceVersion = readString(env, 'VITE_APP_VERSION')

  // The API endpoints are read from the vars the app already uses (see
  // `.env.example`) rather than a telemetry-specific copy, so the propagation
  // allowlist cannot drift away from where the app actually sends requests.
  const apiUrls = [
    readString(env, 'VITE_GRAPHQL_URL') ?? '/graphql',
    readString(env, 'VITE_SIGNALR_NOTIFICATIONS_URL') ?? '/hubs/notifications',
  ]

  return {
    tracesEndpoint,
    headers: parseHeaders(readString(env, 'VITE_OTEL_EXPORTER_OTLP_HEADERS')),
    serviceName: readString(env, 'VITE_OTEL_SERVICE_NAME') ?? DEFAULT_SERVICE_NAME,
    ...(serviceVersion ? { serviceVersion } : {}),
    apiUrls,
  }
}
