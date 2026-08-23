import { ReactNodeViewRenderer } from '@tiptap/react'
import { DrawioDiagram } from './DrawioDiagram'
import { DrawioDiagramView } from './DrawioDiagramView'

/**
 * DrawioDiagram plus its NodeView — same node name/attrs/schema, rendering
 * only (the Image → AttachmentImage pattern; see extensions.ts's comment).
 */
export const DrawioDiagramWithView = DrawioDiagram.extend({
  addNodeView() {
    return ReactNodeViewRenderer(DrawioDiagramView)
  },
})
