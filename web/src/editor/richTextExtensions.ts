import Image from '@tiptap/extension-image'
import { codeBlockExtension, editorExtensions } from './extensions'
import { AttachmentImage } from './nodes/AttachmentImage'
import { MermaidCodeBlock } from './nodes/MermaidCodeBlock'
import { DrawioDiagram } from './nodes/DrawioDiagram'
import { DrawioDiagramWithView } from './nodes/DrawioDiagramWithView'
import { GitLabIssueLink } from './marks/GitLabIssueLink'
import { GitLabIssueLinkWithView } from './marks/GitLabIssueLinkWithView'
import { EmojiDecorations } from './emoji/EmojiDecorations'
import { EmojiSuggestion } from './emoji/EmojiSuggestion'

/**
 * The extension list the real editor mounts (its own module, not
 * RichTextEditor.tsx, so coedit/collabExtensions.ts can build on it
 * without importing a component file — and so the component file keeps
 * fast refresh).
 *
 * Same node names/attrs/schema as the plain extensions used by
 * `editorExtensions` (see extensions.ts's comment) — only the rendering
 * differs, so these swaps never affect what gets serialized.
 *
 * The two emoji extensions appended at the end are schema-free (a
 * decoration renderer and the `:` autocomplete — see editor/emoji/): they
 * belong here with the other network-touching render concerns, not in the
 * shared `editorExtensions` list the round-trip suite validates, precisely
 * because they can never affect what gets serialized.
 */
export const richTextExtensions = [
  ...editorExtensions.map((ext) => {
    if (ext === Image) return AttachmentImage
    if (ext === codeBlockExtension) return MermaidCodeBlock
    if (ext === DrawioDiagram) return DrawioDiagramWithView
    if (ext === GitLabIssueLink) return GitLabIssueLinkWithView
    return ext
  }),
  EmojiDecorations,
  EmojiSuggestion,
]
