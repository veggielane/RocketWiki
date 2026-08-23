import { forwardRef, useEffect, useImperativeHandle, useState } from 'react'
import { EditorContent, useEditor } from '@tiptap/react'
import Image from '@tiptap/extension-image'
import type { EditorView } from '@tiptap/pm/view'
import { Alert, Box, Paper, Snackbar } from '@mui/material'
import { codeBlockExtension, editorExtensions } from './extensions'
import { AttachmentImage } from './nodes/AttachmentImage'
import { MermaidCodeBlock } from './nodes/MermaidCodeBlock'
import { DrawioDiagram } from './nodes/DrawioDiagram'
import { DrawioDiagramWithView } from './nodes/DrawioDiagramWithView'
import { GitLabIssueLink } from './marks/GitLabIssueLink'
import { GitLabIssueLinkWithView } from './marks/GitLabIssueLinkWithView'
import { EmojiDecorations } from './emoji/EmojiDecorations'
import { EmojiSuggestion } from './emoji/EmojiSuggestion'
import { EmojiSuggestionPopup } from './emoji/EmojiSuggestionPopup'
import { markdownToJson } from './markdown/fromMarkdown'
import { jsonToMarkdown } from './markdown/toMarkdown'
import { EditorToolbar } from './EditorToolbar'
import { uploadAttachment } from '../attachments/attachmentApi'
import { computeHeadingAnchors, type HeadingInfo } from './headingAnchors'
import './editor-content.css'

// Same node names/attrs/schema as the plain extensions used by
// `editorExtensions` (see extensions.ts's comment) — only the rendering
// differs, so these swaps never affect what gets serialized.
//
// The two emoji extensions appended at the end are schema-free (a
// decoration renderer and the `:` autocomplete — see editor/emoji/): they
// belong here with the other network-touching render concerns, not in the
// shared `editorExtensions` list the round-trip suite validates, precisely
// because they can never affect what gets serialized.
const richTextExtensions = [
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

export interface RichTextEditorHandle {
  /** Current document, serialized back to Markdown for saving. */
  getMarkdown: () => string
}

export interface RichTextEditorProps {
  /** Markdown as loaded from `Page.content` (design.md §8). */
  initialMarkdown: string
  editable?: boolean
  onChange?: (markdown: string) => void
  /** Hidden in read-only / view mode. */
  showToolbar?: boolean
  /**
   * Required to support drag-and-drop / paste image upload — without it,
   * dropped images are simply left for the browser's default handling
   * (which does nothing useful with a bare `File`, so it's a silent no-op,
   * not a broken feature) rather than half-implementing an upload with
   * nowhere to attach the resulting attachment.
   */
  pageId?: string
}

/**
 * The shared editor component (design.md §4's "one renderer" rule — this is
 * also what page *view* mode should render through, with `editable: false`,
 * so there is never a second Markdown rendering path to drift out of sync).
 */
export const RichTextEditor = forwardRef<RichTextEditorHandle, RichTextEditorProps>(function RichTextEditor(
  { initialMarkdown, editable = true, onChange, showToolbar = true, pageId },
  ref,
) {
  const [uploadError, setUploadError] = useState<string | null>(null)

  const editor = useEditor({
    extensions: richTextExtensions,
    content: markdownToJson(initialMarkdown),
    editable,
    editorProps: {
      attributes: {
        class: 'rw-editor-content',
        role: 'textbox',
        'aria-multiline': 'true',
        'aria-label': 'Page content',
      },
      handleDrop: (view, event, _slice, moved) => {
        if (moved || !pageId) return false
        const files = Array.from(event.dataTransfer?.files ?? []).filter((f) => f.type.startsWith('image/'))
        if (files.length === 0) return false
        event.preventDefault()
        const pos = view.posAtCoords({ left: event.clientX, top: event.clientY })?.pos ?? view.state.selection.from
        for (const file of files) {
          void uploadAndInsert(view, pageId, file, pos, setUploadError)
        }
        return true
      },
      handlePaste: (view, event) => {
        if (!pageId) return false
        const files = Array.from(event.clipboardData?.files ?? []).filter((f) => f.type.startsWith('image/'))
        if (files.length === 0) return false
        event.preventDefault()
        for (const file of files) {
          void uploadAndInsert(view, pageId, file, view.state.selection.from, setUploadError)
        }
        return true
      },
    },
    onUpdate: ({ editor: updated }) => {
      onChange?.(jsonToMarkdown(updated.getJSON()))
    },
  })

  useImperativeHandle(
    ref,
    () => ({
      getMarkdown: () => (editor ? jsonToMarkdown(editor.getJSON()) : initialMarkdown),
    }),
    [editor, initialMarkdown],
  )

  // Deep-link targets for search results (design.md §9) — stable, path-derived
  // ids assigned to rendered headings (see headingAnchors.ts). Re-runs on every
  // update so edited/reordered headings keep correct anchors while editing,
  // not just once at initial render.
  useEffect(() => {
    if (!editor) return

    const assignAnchors = () => {
      const headings: HeadingInfo[] = []
      editor.state.doc.descendants((node) => {
        if (node.type.name === 'heading') {
          headings.push({ level: node.attrs.level as number, text: node.textContent })
        }
      })
      const anchors = computeHeadingAnchors(headings)
      const headingEls = editor.view.dom.querySelectorAll('h1, h2, h3, h4, h5, h6')
      headingEls.forEach((el, i) => {
        const anchor = anchors[i]
        if (anchor) el.id = anchor
      })
    }

    assignAnchors()
    editor.on('update', assignAnchors)
    return () => {
      editor.off('update', assignAnchors)
    }
  }, [editor])

  return (
    <Paper variant="outlined" sx={{ overflow: 'hidden' }}>
      {editable && showToolbar && <EditorToolbar editor={editor} />}
      <Box sx={{ p: 2 }}>
        <EditorContent editor={editor} />
      </Box>
      {editable && editor && <EmojiSuggestionPopup editor={editor} />}
      <Snackbar open={Boolean(uploadError)} autoHideDuration={5000} onClose={() => setUploadError(null)}>
        <Alert severity="error" onClose={() => setUploadError(null)}>
          {uploadError}
        </Alert>
      </Snackbar>
    </Paper>
  )
})

/**
 * Uploads the dropped/pasted file, then inserts an image node pointing at
 * the resulting attachment. Dispatches directly against the ProseMirror
 * `view` handed to us by handleDrop/handlePaste rather than going through
 * `editor.chain()` — by the time the upload resolves, the document may
 * have changed further, but `pos` was captured at drop time the same way
 * `editor.chain()` would capture it anyway, and using `view` directly
 * avoids needing the outer `editor` closure (which doesn't exist yet at
 * the point `editorProps` is being built) or any shared mutable state
 * that would break with more than one editor instance mounted at once —
 * e.g. once each comment in a thread gets its own `RichTextEditor`.
 */
async function uploadAndInsert(
  view: EditorView,
  pageId: string,
  file: File,
  pos: number,
  onError: (message: string) => void,
): Promise<void> {
  try {
    const attachment = await uploadAttachment(pageId, file)
    const imageType = view.state.schema.nodes.image
    if (!imageType) return
    const node = imageType.create({ src: `attachment://${attachment.id}`, alt: file.name })
    view.dispatch(view.state.tr.insert(pos, node))
  } catch {
    onError(`Couldn't upload "${file.name}" — there's no live API in this environment yet.`)
  }
}
