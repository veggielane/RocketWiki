/**
 * The ```drawio fence payload: base64 of the *editable SVG* that the
 * diagrams.net embed editor exports (`format: 'xmlsvg'`). The SVG has the
 * diagram's mxfile XML embedded in its `content` attribute, so one payload
 * serves both purposes:
 *   - display: `<img src="data:image/svg+xml;base64,{payload}">` with zero
 *     viewer JS (and inert — SVG loaded through an <img> element runs no
 *     scripts and loads no external resources, per the HTML spec's
 *     SVG-as-image rules; never render it via <iframe>/<object>, which
 *     would execute it),
 *   - re-editing: the same data URI loads straight back into the embed
 *     editor's `load` action, which documents "SVG data URIs with UTF-8 or
 *     base64 encoding" as an accepted `xml` value.
 */

/**
 * Per-diagram cap on the base64 payload, i.e. on the characters a single
 * diagram adds to the page's Markdown. Editable-SVG exports of typical
 * boxes-and-arrows diagrams measure in the tens of KB (a ~10-shape diagram
 * exports around 20–40 KB of SVG ≈ 30–55 KB base64); 512 KB is roomy for
 * real diagrams while still keeping a page with several of them loadable,
 * diffable, and syncable (design.md §12 bundles carry page markdown).
 */
export const DRAWIO_MAX_PAYLOAD_CHARS = 512 * 1024

export type DecodedDrawioPayload =
  | { ok: true; dataUri: string }
  | { ok: false; reason: string }

const BASE64_SHAPE = /^[A-Za-z0-9+/=\s]+$/

/**
 * Validates a fence payload and produces the `<img>`-ready data URI.
 * Anything hand-mangled (or a plain code block someone typed with the
 * reserved `drawio` language) fails here and renders as an inline error —
 * the raw payload always survives untouched in the Markdown either way.
 */
export function decodeDrawioPayload(payload: string): DecodedDrawioPayload {
  // Whitespace (line breaks in a hand-wrapped fence) is legal base64
  // formatting; strip it for decoding/display, but note the *stored*
  // payload is whatever the fence contains, byte for byte.
  const compact = payload.replace(/\s+/g, '')
  if (compact.length === 0) {
    return { ok: false, reason: 'The diagram payload is empty.' }
  }
  if (!BASE64_SHAPE.test(payload)) {
    return { ok: false, reason: 'The diagram payload is not valid base64.' }
  }

  let text: string
  try {
    const bytes = Uint8Array.from(atob(compact), (c) => c.charCodeAt(0))
    text = new TextDecoder().decode(bytes)
  } catch {
    return { ok: false, reason: 'The diagram payload is not valid base64.' }
  }

  if (!/<svg[\s>]/i.test(text.slice(0, 4096))) {
    return { ok: false, reason: 'The diagram payload is not an SVG export.' }
  }

  return { ok: true, dataUri: `data:image/svg+xml;base64,${compact}` }
}
