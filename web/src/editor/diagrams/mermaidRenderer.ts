/**
 * The one seam between the editor and the mermaid library.
 *
 * Mermaid is *large* (multi-MB of parser + layout code), so it is loaded
 * with a dynamic `import()` — the same pattern as the telemetry SDK chunk
 * (see telemetry/startBrowserTelemetry.ts): Vite splits it into its own
 * chunk and a page with no ```mermaid fence never downloads it.
 *
 * Security (design.md §15 / the page-content trust boundary): mermaid turns
 * user-typed text into inline SVG that we insert into the live document, so
 * it is initialized with `securityLevel: 'strict'` — mermaid's most
 * restrictive mode, which sanitizes labels via its bundled DOMPurify,
 * HTML-encodes tags in text, and disables `click` interactivity. The
 * remaining XSS surface is mermaid's own sanitizer (a sanitizer bypass in
 * mermaid/DOMPurify would reach the page); that is the accepted, industry-
 * standard posture for mermaid rendering — never widen this to 'loose'.
 *
 * Tests mock THIS module (vi.mock of this file), not mermaid itself:
 * mermaid's real renderer needs SVG text-measurement APIs jsdom does not
 * implement, so real rendering is only exercised in a browser.
 */

export type MermaidRenderResult =
  | { status: 'ok'; svg: string }
  | { status: 'error'; message: string }

type MermaidModule = typeof import('mermaid').default

let mermaidLoad: Promise<MermaidModule> | null = null
let renderSeq = 0

async function loadMermaid(): Promise<MermaidModule> {
  const { default: mermaid } = await import('mermaid')
  mermaid.initialize({
    startOnLoad: false,
    securityLevel: 'strict',
    // 'neutral' reads on both light and dark app themes because the preview
    // surface behind it is always light (see .rw-diagram-surface).
    theme: 'neutral',
    fontFamily: 'inherit',
  })
  return mermaid
}

/** Renders mermaid source to an SVG string; never throws, never crashes the page. */
export async function renderMermaid(source: string): Promise<MermaidRenderResult> {
  const id = `rw-mermaid-${renderSeq++}`
  try {
    const mermaid = await (mermaidLoad ??= loadMermaid())
    const { svg } = await mermaid.render(id, source)
    return { status: 'ok', svg }
  } catch (error) {
    // On a parse/render failure mermaid can leave its scratch element
    // behind in <body>; remove it so failed previews don't accumulate junk.
    document.getElementById(`d${id}`)?.remove()
    return { status: 'error', message: error instanceof Error ? error.message : String(error) }
  }
}
