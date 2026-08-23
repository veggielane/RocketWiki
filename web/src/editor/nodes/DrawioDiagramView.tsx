import { useState } from 'react'
import { NodeViewWrapper } from '@tiptap/react'
import type { NodeViewProps } from '@tiptap/core'
import { decodeDrawioPayload } from '../drawio/drawioPayload'
import { DrawioEditorDialog } from '../drawio/DrawioEditorDialog'

/**
 * Renders the drawioDiagram node (see DrawioDiagram.ts for the format).
 * Display is a plain `<img>` with a data URI — an <img> never executes
 * scripts inside an SVG (SVG-as-image), which is why display deliberately
 * is NOT an <iframe>/<object>. The Edit button (edit mode only — the page
 * view's read-only editor never offers it) opens the embedded diagrams.net
 * editor; a failed/garbled payload renders an inline error and leaves the
 * stored Markdown untouched.
 */
export function DrawioDiagramView({ node, editor, updateAttributes }: NodeViewProps) {
  const payload = (node.attrs.payload as string) ?? ''
  const editable = editor.isEditable
  const [editorOpen, setEditorOpen] = useState(false)
  const decoded = payload.trim().length > 0 ? decodeDrawioPayload(payload) : null

  return (
    <NodeViewWrapper className="rw-drawio" data-diagram="drawio">
      {decoded === null && (
        <div className="rw-diagram-hint">
          {editable ? 'Empty draw.io diagram — use "Edit diagram" to draw it.' : 'Empty draw.io diagram.'}
        </div>
      )}
      {decoded?.ok === true && <img className="rw-drawio-img" src={decoded.dataUri} alt="draw.io diagram" />}
      {decoded?.ok === false && (
        <div role="alert" className="rw-diagram-error">
          <strong>Diagram can't be displayed.</strong> {decoded.reason} The payload itself is still stored
          in the page content.
        </div>
      )}
      {editable && (
        <div className="rw-drawio-actions" contentEditable={false}>
          <button type="button" className="rw-diagram-button" onClick={() => setEditorOpen(true)}>
            Edit diagram
          </button>
        </div>
      )}
      {editable && editorOpen && (
        <DrawioEditorDialog
          open={editorOpen}
          // A payload that doesn't decode would only confuse the embed
          // editor's `load` — start it blank instead; saving then replaces
          // the broken payload deliberately, via the user, not silently.
          payload={decoded?.ok ? payload : ''}
          onSave={(next) => {
            updateAttributes({ payload: next })
            setEditorOpen(false)
          }}
          onClose={() => setEditorOpen(false)}
        />
      )}
    </NodeViewWrapper>
  )
}
