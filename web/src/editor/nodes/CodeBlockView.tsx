import { useEffect, useRef, useState } from 'react'
import { NodeViewContent, NodeViewWrapper } from '@tiptap/react'
import type { NodeViewProps } from '@tiptap/core'
import { renderMermaid, type MermaidRenderResult } from '../diagrams/mermaidRenderer'

/**
 * NodeView for every `codeBlock`. Non-mermaid languages render exactly as
 * the stock extension would (a `pre > code` with the same class); a
 * ` ```mermaid ` block additionally gets a live rendered preview:
 *   - editing: source beside preview (stacked on narrow screens), preview
 *     re-rendered debounced as you type,
 *   - read mode (page view — same component, `editable: false`, per the
 *     one-renderer rule): the diagram alone, unless it fails to render, in
 *     which case the source is shown next to an inline error block so the
 *     content is never invisible.
 *
 * The *Markdown* form stays a plain fenced code block either way — this
 * file changes rendering only, never serialization.
 */
export function CodeBlockView(props: NodeViewProps) {
  const language = (props.node.attrs.language as string | null) ?? null
  if (language === 'mermaid') {
    return <MermaidBlock {...props} />
  }
  return (
    <NodeViewWrapper>
      <pre className="rw-code-block" spellCheck={false}>
        <NodeViewContent<'code'> as="code" />
      </pre>
    </NodeViewWrapper>
  )
}

const PREVIEW_DEBOUNCE_MS = 300

function MermaidBlock({ node, editor }: NodeViewProps) {
  const source = node.textContent
  const editable = editor.isEditable
  const [result, setResult] = useState<MermaidRenderResult | null>(null)
  const ticketRef = useRef(0)
  const firstRenderRef = useRef(true)

  useEffect(() => {
    const ticket = ++ticketRef.current
    // Empty source renders nothing ('empty' is derived at render time, so
    // no state write is needed here) — bumping the ticket above already
    // discards any in-flight render of the previous text.
    if (source.trim().length === 0) return
    // First render (page view, or a block scrolling into an open document)
    // draws immediately; only *typing* is debounced.
    const delay = firstRenderRef.current ? 0 : PREVIEW_DEBOUNCE_MS
    firstRenderRef.current = false
    const timer = setTimeout(() => {
      void renderMermaid(source).then((next) => {
        // A newer source superseded this render — drop the stale result.
        if (ticketRef.current === ticket) setResult(next)
      })
    }, delay)
    return () => clearTimeout(timer)
  }, [source])

  const renderState = source.trim().length === 0 ? 'empty' : (result?.status ?? 'pending')

  return (
    <NodeViewWrapper
      className={`rw-mermaid-block ${editable ? 'rw-mermaid-block-editing' : 'rw-mermaid-block-readonly'}`}
      data-render-state={renderState}
    >
      <pre className="rw-code-block rw-mermaid-source" spellCheck={false}>
        <NodeViewContent<'code'> as="code" />
      </pre>
      <div className="rw-mermaid-preview" contentEditable={false}>
        {renderState === 'empty' && (
          <div className="rw-diagram-hint">Type Mermaid source to see a diagram preview.</div>
        )}
        {renderState === 'pending' && <div className="rw-diagram-hint">Rendering diagram…</div>}
        {renderState === 'ok' && result?.status === 'ok' && (
          <div
            className="rw-diagram-surface"
            role="img"
            aria-label="Mermaid diagram"
            // The SVG comes from mermaid running with securityLevel:
            // 'strict' (see diagrams/mermaidRenderer.ts) — that sanitizer
            // is the trust boundary for this injection point.
            dangerouslySetInnerHTML={{ __html: result.svg }}
          />
        )}
        {renderState === 'error' && result?.status === 'error' && (
          <div role="alert" className="rw-diagram-error">
            <strong>Diagram doesn't render.</strong> {result.message}
          </div>
        )}
      </div>
    </NodeViewWrapper>
  )
}
