import { useEffect, useRef, useState, type ReactNode } from 'react'
import { NodeViewContent, NodeViewWrapper } from '@tiptap/react'
import type { NodeViewProps } from '@tiptap/core'
import { renderMermaid, type MermaidRenderResult } from '../diagrams/mermaidRenderer'
import { hasMermaidAccDirectives, mermaidFallbackLabel } from '../diagrams/mermaidAccessibility'
import { parseFileFence, parseIssuesFence } from '../../gitlab/fenceBody'
import { describeDiagramUnavailable } from '../../feedback/unavailableCopy'
import { GitLabFileBlock } from '../../gitlab/GitLabFileBlock'
import { GitLabIssuesBlock } from '../../gitlab/GitLabIssuesBlock'

/**
 * NodeView for every `codeBlock`. Ordinary languages render exactly as the
 * stock extension would (a `pre > code` with the same class); the reserved
 * fence languages additionally get a live rendered preview:
 *   - ` ```mermaid ` — the diagram,
 *   - ` ```gitlab-file ` / ` ```gitlab-issues ` — live GitLab embeds
 *     (design.md §18), fetched through the API only,
 * with the shared layout: editing shows source beside preview (stacked on
 * narrow screens, preview debounced as you type); read mode (page view —
 * same component, `editable: false`, per the one-renderer rule) shows the
 * preview alone, unless the source doesn't parse, in which case the source
 * comes back so the content is never invisible.
 *
 * The *Markdown* form stays a plain fenced code block for all of them —
 * this file changes rendering only, never serialization.
 */
export function CodeBlockView(props: NodeViewProps) {
  const language = (props.node.attrs.language as string | null) ?? null
  if (language === 'mermaid') {
    return <MermaidBlock {...props} />
  }
  if (language === 'gitlab-file') {
    return <GitLabFileFence {...props} />
  }
  if (language === 'gitlab-issues') {
    return <GitLabIssuesFence {...props} />
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
            // With accTitle/accDescr in the source, mermaid wires the name
            // onto the <svg> itself and the wrapper must stay role-less —
            // role="img" here would demote the SVG to a presentational
            // child and silence the author's text. Without them, fall back
            // to a label naming the diagram type. See
            // diagrams/mermaidAccessibility.ts.
            {...(hasMermaidAccDirectives(source)
              ? {}
              : { role: 'img', 'aria-label': mermaidFallbackLabel(source) })}
            // The SVG comes from mermaid running with securityLevel:
            // 'strict' (see diagrams/mermaidRenderer.ts) — that sanitizer
            // is the trust boundary for this injection point.
            dangerouslySetInnerHTML={{ __html: result.svg }}
          />
        )}
        {renderState === 'error' && result?.status === 'error' && (
          <div role="alert" className="rw-diagram-error">
            <strong>{describeDiagramUnavailable('MERMAID_SOURCE').summary}</strong> {result.message}
          </div>
        )}
      </div>
    </NodeViewWrapper>
  )
}

/**
 * While typing in a gitlab fence, wait for the keystrokes to settle before
 * re-parsing (and therefore re-fetching — each variables change is a live
 * API call). Read mode passes 0: the source is static, render immediately.
 */
function useDebouncedValue(value: string, delayMs: number): string {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    if (delayMs === 0) return
    const timer = setTimeout(() => setDebounced(value), delayMs)
    return () => clearTimeout(timer)
  }, [value, delayMs])
  // Zero delay derives directly — no state write, no extra render.
  return delayMs === 0 ? value : debounced
}

function GitLabFileFence({ node, editor }: NodeViewProps) {
  const editable = editor.isEditable
  const source = useDebouncedValue(node.textContent, editable ? PREVIEW_DEBOUNCE_MS : 0)
  const parsed = parseFileFence(source)
  return (
    <GitLabFenceLayout editable={editable} ok={parsed.ok}>
      {parsed.ok ? (
        <GitLabFileBlock fileRef={parsed.ref} />
      ) : (
        <GitLabFenceIncomplete kind="gitlab-file" missing={parsed.missing} />
      )}
    </GitLabFenceLayout>
  )
}

function GitLabIssuesFence({ node, editor }: NodeViewProps) {
  const editable = editor.isEditable
  const source = useDebouncedValue(node.textContent, editable ? PREVIEW_DEBOUNCE_MS : 0)
  const parsed = parseIssuesFence(source)
  return (
    <GitLabFenceLayout editable={editable} ok={parsed.ok}>
      {parsed.ok ? (
        <GitLabIssuesBlock spec={parsed.spec} />
      ) : (
        <GitLabFenceIncomplete kind="gitlab-issues" missing={parsed.missing} />
      )}
    </GitLabFenceLayout>
  )
}

/**
 * Shared frame for both gitlab fences, mirroring MermaidBlock: the source
 * (ProseMirror's contentDOM) always stays in the DOM; CSS shows it in edit
 * mode and hides it in read mode unless the fence doesn't parse
 * (`data-render-state="error"` brings it back so content is never
 * invisible).
 */
function GitLabFenceLayout({
  editable,
  ok,
  children,
}: {
  editable: boolean
  ok: boolean
  children: ReactNode
}) {
  return (
    <NodeViewWrapper
      className={`rw-gitlab-block ${editable ? 'rw-gitlab-block-editing' : 'rw-gitlab-block-readonly'}`}
      data-render-state={ok ? 'ok' : 'error'}
    >
      <pre className="rw-code-block rw-gitlab-source" spellCheck={false}>
        <NodeViewContent<'code'> as="code" />
      </pre>
      <div className="rw-gitlab-preview" contentEditable={false}>
        {children}
      </div>
    </NodeViewWrapper>
  )
}

function GitLabFenceIncomplete({ kind, missing }: { kind: string; missing: string[] }) {
  return (
    <div className="rw-diagram-hint">
      Incomplete {kind} reference — missing {missing.join(', ')}. Body is key=value lines.
    </div>
  )
}
