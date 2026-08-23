import { readTelemetryConfig } from './config'
import type { TelemetryHandle } from './tracing'

/**
 * The one entry point the app calls (from `main.tsx`). Everything about
 * browser telemetry is behind this gate:
 *
 *   - No OTLP endpoint configured → returns null having imported nothing. The
 *     tracing SDK is a dynamic `import()`, so Vite splits it into its own
 *     chunk and an unconfigured deployment never downloads or parses it.
 *   - Anything at all goes wrong starting it → warn and continue. Telemetry is
 *     operational data (design.md §15); it is never worth failing the wiki for.
 *
 * Deliberately not awaited by `main.tsx`: tracing must not sit between the
 * user and first paint. Starting late costs nothing, because document-load
 * timings are read retrospectively from the Performance Timeline rather than
 * observed live.
 */
export async function startBrowserTelemetry(): Promise<TelemetryHandle | null> {
  const config = readTelemetryConfig(import.meta.env)
  if (!config) return null

  try {
    const { initTracing } = await import('./tracing')
    return initTracing(config)
  } catch (error) {
    console.warn('[telemetry] browser tracing failed to start; continuing without it', error)
    return null
  }
}
