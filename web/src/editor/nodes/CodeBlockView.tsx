import { useEffect, useRef, useState, type ReactNode } from 'react'
import { NodeViewContent, NodeViewWrapper } from '@tiptap/react'
import type { NodeViewProps } from '@tiptap/core'
import { renderMermaid, type MermaidRenderResult } from '../diagrams/mermaidRenderer'
import { hasMermaidAccDirectives, mermaidFallbackLabel } from '../diagrams/mermaidAccessibility'
import { parseFileFence, parseIssuesFence } from '../../gitlab/fenceBody'
import { parsePageListFence } from '../../pagelist/fenceBody'
import { describeDiagramUnavailable } from '../../feedback/unavailableCopy'
import { GitLabFileBlock } from '../../gitlab/GitLabFileBlock'
import { GitLabIssuesBlock } from '../../gitlab/GitLabIssuesBlock'
import { PageListBlock } from '../../pagelist/PageListBlock'
import { FormDefinitionBlock, FormListBlock } from '../../forms/FormBlocks'
import { parseFormFence } from '../../forms/formFence'
import { useDebouncedValue } from '../useDebouncedValue'

/**
 * NodeView for every `codeBlock`. Ordinary languages render exactly as the
 * stock extension would (a `pre > code` with the same class); the reserved
 * fence languages additionally get a live rendered preview:
 *   - ` ```mermaid ` — the diagram,
 *   - ` ```gitlab-file ` / ` ```gitlab-issues ` — live GitLab embeds
 *     (design.md §18), fetched through the API only,
 *   - ` ```page-list ` — the pages matching an RQL query (design.md §22),
 * with the shared layout: editing shows source beside preview (stacked on
 * narrow screens, preview debounced as you type); read mode (page view —
 * same component, `editable: false`, per the one-renderer rule) shows the
 * preview alone, unless the source doesn't parse, in which case the source
 * comes back so the content is never invisible.
 *
 * The *Markdown* form stays a plain fenced code block for all of them —
 * this file changes rendering only, never serialization. This chain is the
 * de-facto registry of reserved fence languages; adding one costs a branch
 * here and nothing in the Markdown pipeline, which is the whole point.
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
  if (language === 'page-list') {
    return <PageListFence {...props} />
  }
  if (language === 'form-definition') {
    return <FormFence {...props} kind="form-definition" />
  }
  if (language === 'form-list') {
    return <FormFence {...props} kind="form-list" />
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

function GitLabFileFence({ node, editor }: NodeViewProps) {
  const editable = editor.isEditable
  const source = useDebouncedValue(node.textContent, editable ? PREVIEW_DEBOUNCE_MS : 0)
  const parsed = parseFileFence(source)
  return (
    <FenceLayout editable={editable} ok={parsed.ok}>
      {parsed.ok ? <GitLabFileBlock fileRef={parsed.ref} /> : <FenceIncomplete kind="gitlab-file" missing={parsed.missing} />}
    </FenceLayout>
  )
}

function GitLabIssuesFence({ node, editor }: NodeViewProps) {
  const editable = editor.isEditable
  const source = useDebouncedValue(node.textContent, editable ? PREVIEW_DEBOUNCE_MS : 0)
  const parsed = parseIssuesFence(source)
  return (
    <FenceLayout editable={editable} ok={parsed.ok}>
      {parsed.ok ? <GitLabIssuesBlock spec={parsed.spec} /> : <FenceIncomplete kind="gitlab-issues" missing={parsed.missing} />}
    </FenceLayout>
  )
}

/**
 * Both form fences, which differ only in what they render from the same spec: the
 * definition fence draws the form to fill in, the list fence draws the records. Sharing
 * the parse is what keeps `collection` meaning one thing in both.
 */
function FormFence({ node, editor, kind }: NodeViewProps & { kind: 'form-definition' | 'form-list' }) {
  const editable = editor.isEditable
  const source = useDebouncedValue(node.textContent, editable ? PREVIEW_DEBOUNCE_MS : 0)
  const parsed = parseFormFence(source)
  return (
    <FenceLayout editable={editable} ok={parsed.ok}>
      {parsed.ok ? (
        kind === 'form-definition' ? (
          <FormDefinitionBlock collection={parsed.spec.collection} />
        ) : (
          <FormListBlock collection={parsed.spec.collection} columns={parsed.spec.columns} />
        )
      ) : (
        <FenceIncomplete kind={kind} missing={parsed.missing} />
      )}
    </FenceLayout>
  )
}

function PageListFence({ node, editor }: NodeViewProps) {
  const editable = editor.isEditable
  const source = useDebouncedValue(node.textContent, editable ? PREVIEW_DEBOUNCE_MS : 0)
  const parsed = parsePageListFence(source)
  return (
    <FenceLayout editable={editable} ok={parsed.ok}>
      {parsed.ok ? <PageListBlock spec={parsed.spec} /> : <FenceIncomplete kind="page-list" missing={parsed.missing} />}
    </FenceLayout>
  )
}

/**
 * Shared frame for every key=value fence, mirroring MermaidBlock: the source
 * (ProseMirror's contentDOM) always stays in the DOM; CSS shows it in edit
 * mode and hides it in read mode unless the fence doesn't parse
 * (`data-render-state="error"` brings it back so content is never
 * invisible).
 *
 * Named for the job rather than for GitLab, which built it: it now frames
 * three fence languages from two unrelated features, and a shared component
 * whose name claims one of its callers is the thing the next caller
 * copy-pastes instead of importing. The CSS classes moved with it.
 */
function FenceLayout({ editable, ok, children }: { editable: boolean; ok: boolean; children: ReactNode }) {
  return (
    <NodeViewWrapper
      className={`rw-fence-block ${editable ? 'rw-fence-block-editing' : 'rw-fence-block-readonly'}`}
      data-render-state={ok ? 'ok' : 'error'}
    >
      <pre className="rw-code-block rw-fence-source" spellCheck={false}>
        <NodeViewContent<'code'> as="code" />
      </pre>
      <div className="rw-fence-preview" contentEditable={false}>
        {children}
      </div>
    </NodeViewWrapper>
  )
}

function FenceIncomplete({ kind, missing }: { kind: string; missing: string[] }) {
  return (
    <div className="rw-diagram-hint">
      Incomplete {kind} reference — missing {missing.join(', ')}. Body is key=value lines.
    </div>
  )
}
