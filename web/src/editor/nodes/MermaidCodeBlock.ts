import CodeBlock from '@tiptap/extension-code-block'
import { ReactNodeViewRenderer } from '@tiptap/react'
import { CodeBlockView } from './CodeBlockView'

/**
 * The `codeBlock` extension the real editor uses: identical
 * name/attrs/schema to the plain `codeBlockExtension` in extensions.ts
 * (same swap pattern as Image → AttachmentImage — rendering only, never
 * serialization), plus a NodeView that live-renders ```mermaid blocks as
 * diagrams (see CodeBlockView.tsx). Kept out of `editorExtensions` so the
 * headless round-trip suite never touches mermaid or React.
 */
export const MermaidCodeBlock = CodeBlock.extend({
  addNodeView() {
    return ReactNodeViewRenderer(CodeBlockView)
  },
}).configure({ HTMLAttributes: { class: 'rw-code-block' } })
