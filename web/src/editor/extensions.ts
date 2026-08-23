import StarterKit from '@tiptap/starter-kit'
import Image from '@tiptap/extension-image'
import CodeBlock from '@tiptap/extension-code-block'
import TaskList from '@tiptap/extension-task-list'
import TaskItem from '@tiptap/extension-task-item'
import { TableKit } from '@tiptap/extension-table'
import type { AnyExtension } from '@tiptap/core'
import { PageLink } from './marks/PageLink'
import { Mention } from './nodes/Mention'
import { Callout } from './nodes/Callout'
import { DrawioDiagram } from './nodes/DrawioDiagram'

/**
 * The full v1 extension set (design.md §4). This list is the schema: it is
 * shared by the real editor component and by the round-trip tests, so a
 * markdown fixture is only ever validated against the schema users will
 * actually type into.
 *
 * Deliberately uses the *plain* `Image` node, not `AttachmentImage`
 * (editor/nodes/AttachmentImage.tsx) — that extension's NodeView fetches
 * the attachment over the network to resolve `attachment://{id}` to a
 * displayable blob URL, which has no business running inside the
 * round-trip suite (headless, no live API, no reason to touch `fetch`).
 * `RichTextEditor.tsx` swaps in `AttachmentImage` for the real editor;
 * both share the exact same node name/attrs/schema, so this swap is purely
 * about *rendering*, never about what gets serialized.
 *
 * The same pattern covers diagrams: `codeBlockExtension` below is the stock
 * code block (pulled out of StarterKit so it's swappable by identity), and
 * `DrawioDiagram` is the schema-only diagram node — RichTextEditor swaps in
 * `MermaidCodeBlock` / `DrawioDiagramWithView`, whose NodeViews live-render
 * ```mermaid fences and draw.io payloads. Neither the mermaid library nor
 * any React rendering ever runs in the headless round-trip suite.
 */

/** Stock code block, extracted from StarterKit so RichTextEditor can swap its rendering by identity. */
export const codeBlockExtension = CodeBlock.configure({
  HTMLAttributes: { class: 'rw-code-block' },
})
export const editorExtensions: AnyExtension[] = [
  StarterKit.configure({
    link: {
      // Regular http(s) links only — page:// links use the separate
      // PageLink mark below so the two can be styled/handled differently.
      autolink: false,
      openOnClick: false,
      protocols: ['http', 'https'],
    },
    // Provided by `codeBlockExtension` above (identical configuration) so
    // the real editor can swap in the mermaid-previewing variant.
    codeBlock: false,
    // No markdown representation exists for underline in the v1 feature
    // set (design.md §4 is GFM + our extensions, and GFM has no underline
    // syntax). Leaving it enabled would let a user apply a mark that
    // silently disappears on save — the round-trip rule says that doesn't
    // ship, so the affordance is removed entirely rather than left as a
    // trap.
    underline: false,
  }),
  codeBlockExtension,
  PageLink,
  Mention,
  Callout,
  DrawioDiagram,
  Image,
  TaskList,
  TaskItem.configure({ nested: true }),
  TableKit.configure({ table: { resizable: false } }),
]
