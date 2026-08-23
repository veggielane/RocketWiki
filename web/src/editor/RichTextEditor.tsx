import { forwardRef, useEffect, useImperativeHandle, useState } from 'react'
import { EditorContent, useEditor } from '@tiptap/react'
import Image from '@tiptap/extension-image'
import Collaboration from '@tiptap/extension-collaboration'
import { CollaborationCaret } from '@tiptap/extension-collaboration-caret'
import type { EditorView } from '@tiptap/pm/view'
import type * as Y from 'yjs'
import type { Awareness } from 'y-protocols/awareness'
import { Alert, Box, Paper, Snackbar } from '@mui/material'
import { codeBlockExtension, editorExtensions } from './extensions'
import { AttachmentImage } from './nodes/AttachmentImage'
import { MermaidCodeBlock } from './nodes/MermaidCodeBlock'
import { DrawioDiagram } from './nodes/DrawioDiagram'
import { DrawioDiagramWithView } from './nodes/DrawioDiagramWithView'
import { GitLabIssueLink } from './marks/GitLabIssueLink'
import { GitLabIssueLinkWithView } from './marks/GitLabIssueLinkWithView'
import { renderCaret } from './coedit/caretRender'
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

/**
 * Live co-editing binding (design.md §8). The doc/awareness pair belongs
 * to a SignalRYjsProvider (editor/coedit/) — `provider` is typed
 * structurally because all CollaborationCaret reads from it is
 * `.awareness`. `user` is this client's caret identity (see
 * coedit/caretRender.ts for what may and may not ride awareness).
 */
export interface CollabBinding {
  doc: Y.Doc
  provider: { awareness: Awareness }
  user: { name: string; color: string; userId?: string }
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
  /**
   * Present = collaborative mode: the document lives in the binding's
   * Y.Doc (seeded/replayed by the provider before this component mounts),
   * so `initialMarkdown` is ignored — setting content AND binding the
   * fragment would duplicate the page. Must be stable for the editor's
   * lifetime; the page keys this component on its session so a mode or
   * session change remounts rather than rebinds.
   */
  collab?: CollabBinding
}

/**
 * Collaborative variant of the extension list: same schema (the swap rule
 * above — schema is `editorExtensions`' alone), plus the Yjs binding.
 * `undoRedo: false` because Collaboration replaces prosemirror-history
 * with the Yjs undo manager — two undo stacks over one document would
 * fight (and Collaboration provides its own undo/redo commands +
 * keybindings, so the editor keeps working undo).
 */
function buildCollabExtensions(collab: CollabBinding) {
  return [
    ...richTextExtensions.map((ext) => (ext.name === 'starterKit' ? ext.configure({ undoRedo: false }) : ext)),
    Collaboration.configure({ document: collab.doc }),
    CollaborationCaret.configure({ provider: collab.provider, user: collab.user, render: renderCaret }),
  ]
}

/**
 * The shared editor component (design.md §4's "one renderer" rule — this is
 * also what page *view* mode should render through, with `editable: false`,
 * so there is never a second Markdown rendering path to drift out of sync).
 */
export const RichTextEditor = forwardRef<RichTextEditorHandle, RichTextEditorProps>(function RichTextEditor(
  { initialMarkdown, editable = true, onChange, showToolbar = true, pageId, collab },
  ref,
) {
  const [uploadError, setUploadError] = useState<string | null>(null)

  const editor = useEditor({
    extensions: collab ? buildCollabExtensions(collab) : richTextExtensions,
    // Collaborative mode: content comes from the Y.Doc fragment, never
    // from here — a second content source would duplicate the document.
    ...(collab ? {} : { content: markdownToJson(initialMarkdown) }),
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

  // `editable` can change mid-lifetime in collaborative mode — eviction
  // (design.md §8: canEdit revoked → EvictedFromEditSession) drops the
  // live editor to read-only without remounting, so the user's text stays
  // on screen to be copied.
  useEffect(() => {
    if (editor && editor.isEditable !== editable) {
      editor.setEditable(editable)
    }
  }, [editor, editable])

  // The caret identity can resolve after mount (the CurrentUser query may
  // still be in flight when the session comes up) — updateUser refreshes
  // the awareness `user` field without recreating the editor.
  useEffect(() => {
    if (editor && collab && editor.isEditable) {
      editor.commands.updateUser(collab.user)
    }
  }, [editor, collab])

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
