import { useState } from 'react'
import { NodeViewWrapper } from '@tiptap/react'
import type { NodeViewProps } from '@tiptap/core'
import { decodeDrawioPayload } from '../drawio/drawioPayload'
import { DrawioEditorDialog } from '../drawio/DrawioEditorDialog'
import { describeDiagramUnavailable } from '../../feedback/unavailableCopy'

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
  const alt = (node.attrs.alt as string) ?? ''
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
      {/* Author-supplied alt when there is one; the generic fallback only
          says what KIND of thing sits here, which is why the editor dialog
          asks for a real description. */}
      {decoded?.ok === true && (
        <img className="rw-drawio-img" src={decoded.dataUri} alt={alt.length > 0 ? alt : 'draw.io diagram'} />
      )}
      {decoded?.ok === false && (
        <div role="alert" className="rw-diagram-error">
          <strong>{describeDiagramUnavailable('DRAWIO_PAYLOAD').summary}</strong> {decoded.reason} The payload
          itself is still stored in the page content.
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
          alt={alt}
          onSave={(next, nextAlt) => {
            updateAttributes({ payload: next, alt: nextAlt })
            setEditorOpen(false)
          }}
          onClose={() => setEditorOpen(false)}
        />
      )}
    </NodeViewWrapper>
  )
}
