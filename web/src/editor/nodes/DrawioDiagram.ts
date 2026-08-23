import { Node, mergeAttributes } from '@tiptap/core'

/**
 * A draw.io (diagrams.net) diagram embedded *inline* in the page.
 *
 * Markdown form (design.md §4's round-trip rule):
 *
 *     ```drawio
 *     PHN2ZyB4bWxucz0i…            ← base64 of the editable SVG export
 *     ```
 *
 * i.e. a plain fenced code block with the reserved language `drawio`, so it
 * flows through the serializer, sync bundles (§12), and the Confluence
 * importer as inert text — nothing outside the SPA needs to understand it.
 * The payload is the diagrams.net `xmlsvg` export: an SVG with the diagram
 * XML embedded, so the single payload both displays as a data-URI <img>
 * (no viewer JS, scripts inert per SVG-as-image rules) and reloads into the
 * embed editor for re-editing. See drawio/drawioPayload.ts.
 *
 * This is the plain schema-only extension used by `editorExtensions` (and
 * therefore the headless round-trip suite); RichTextEditor swaps in
 * `DrawioDiagramWithView` for actual rendering, exactly like
 * Image → AttachmentImage.
 */
export const DrawioDiagram = Node.create({
  name: 'drawioDiagram',
  group: 'block',
  atom: true,

  addAttributes() {
    return {
      payload: {
        default: '',
        parseHTML: (element) => element.getAttribute('data-drawio-payload') ?? '',
        renderHTML: (attributes: Record<string, unknown>) => ({
          'data-drawio-payload': attributes.payload,
        }),
      },
    }
  },

  parseHTML() {
    return [{ tag: 'div[data-drawio-payload]' }]
  },

  renderHTML({ HTMLAttributes }) {
    return ['div', mergeAttributes(HTMLAttributes, { class: 'rw-drawio' })]
  },
})
