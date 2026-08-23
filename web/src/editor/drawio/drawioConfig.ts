/**
 * Where the embedded diagrams.net (draw.io) editor comes from.
 *
 * **Fail closed, like telemetry** (design.md §15, and telemetry/config.ts's
 * precedent): with `VITE_DRAWIO_URL` unset there is NO default editor URL —
 * the SPA loads nothing external, and the edit dialog explains how to
 * configure one instead. Editing a diagram means posting its full content
 * into whatever page that URL serves, so "forgot to configure it" must mean
 * "no editor", never "quietly use the public internet service". Viewing is
 * unaffected: diagrams are stored inline in the page Markdown and render as
 * plain data-URI images with no editor at all.
 *
 * Production must point at a self-hosted in-network instance (the AppHost
 * defines a dev-only `drawio` container). Pointing at the public service is
 * a deliberate dev-only opt-in (see web/.env.example) and is flagged as
 * `publicService` so the dialog can show a visible external-editor warning.
 */

export interface DrawioConfig {
  /** Base URL of the diagrams.net editor to embed, as configured. */
  url: string
  /** True when the URL is the public internet service (diagrams.net / draw.io), not something self-hosted. */
  publicService: boolean
}

/** Environment shape this module reads. `import.meta.env` satisfies it. */
export type DrawioEnv = Record<string, unknown>

export function readDrawioConfig(env: DrawioEnv): DrawioConfig | null {
  const raw = env['VITE_DRAWIO_URL']
  if (typeof raw !== 'string') return null
  const trimmed = raw.trim()
  if (trimmed.length === 0) return null

  try {
    const parsed = new URL(trimmed)
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null
    const host = parsed.hostname.toLowerCase()
    const publicService =
      host === 'diagrams.net' ||
      host.endsWith('.diagrams.net') ||
      host === 'draw.io' ||
      host.endsWith('.draw.io')
    return { url: parsed.toString(), publicService }
  } catch {
    // A typo'd URL disables the editor rather than handing an unparseable
    // string to an iframe — same posture as telemetry's endpoint parsing.
    return null
  }
}

/** The iframe `src`: the configured URL plus the JSON embed-protocol switches. */
export function buildEmbedUrl(config: DrawioConfig): string {
  const url = new URL(config.url)
  url.searchParams.set('embed', '1')
  url.searchParams.set('proto', 'json')
  url.searchParams.set('spin', '1')
  // One "Save and Exit" button instead of separate Save/Exit — the modal
  // treats a save as save-and-close, so two buttons would misrepresent it.
  url.searchParams.set('noSaveBtn', '1')
  return url.toString()
}

/** The origin `postMessage` traffic is sent to and accepted from. */
export function embedOrigin(config: DrawioConfig): string {
  return new URL(config.url).origin
}
