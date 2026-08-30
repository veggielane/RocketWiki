import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react'
import { Extension, type AnyExtension } from '@tiptap/core'
import { EditorContent, useEditor } from '@tiptap/react'
import type { EditorView } from '@tiptap/pm/view'
import type * as Y from 'yjs'
import type { Awareness } from 'y-protocols/awareness'
import { Alert, Box, Paper } from '@mui/material'
import { richTextExtensions } from './richTextExtensions'
import { EmojiSuggestionPopup } from './emoji/EmojiSuggestionPopup'
import { markdownToJson } from './markdown/fromMarkdown'
import { jsonToMarkdown } from './markdown/toMarkdown'
import { EditorToolbar } from './EditorToolbar'
import { uploadAttachment } from '../attachments/attachmentApi'
import { describeAttachmentUnavailable } from '../feedback/unavailableCopy'
import { computeHeadingAnchors, type HeadingInfo } from './headingAnchors'
import './editor-content.css'

export interface RichTextEditorHandle {
  /** Current document, serialized back to Markdown for saving. */
  getMarkdown: () => string
  /**
   * Empties the document — for a composer that has just submitted.
   *
   * The comment composer used to appear to clear itself, but only because
   * posting refetched the page and the `fetching` guard remounted the whole
   * subtree underneath it. Once that teardown stopped (the screens now keep
   * their data across a refetch), the text stayed in the box. Clearing has to
   * be something the composer asks for.
   */
  clear: () => void
}

/**
 * Live co-editing binding (design.md §8). The doc/awareness pair belongs
 * to a SignalRYjsProvider (editor/coedit/) — `provider` is typed
 * structurally because all CollaborationCaret reads from it is
 * `.awareness`. `user` is this client's caret identity (see
 * coedit/caretRender.ts for what may and may not ride awareness).
 *
 * `extensionsModule` is the dynamically-loaded coedit/collabExtensions.ts:
 * the CRDT machinery must never be statically reachable from this file
 * (the one-renderer rule means every read-only page VIEW mounts this
 * component, and a static Collaboration import dragged yjs/y-prosemirror —
 * a multi-hundred-KB chunk — into the view path where it can never run;
 * same air-gap splitting rationale as app/router.tsx). Whoever produces a
 * binding has necessarily already loaded the module — useCoEditSession
 * fetches it alongside the provider, before the join that yields the
 * binding resolves — so the editor still mounts synchronously.
 */
export interface CollabBinding {
  doc: Y.Doc
  provider: { awareness: Awareness }
  user: { name: string; color: string; userId?: string }
  extensionsModule: CollabExtensionsModule
}

/** Shape of `import('./coedit/collabExtensions')` — carried on the binding, see above. */
export interface CollabExtensionsModule {
  buildCollabExtensions: (collab: CollabBinding) => AnyExtension[]
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
  /**
   * Composer mode (comments): fired on Ctrl/Cmd+Enter. Registered as a
   * TipTap keyboard shortcut, not a DOM listener on a wrapper — StarterKit's
   * HardBreak already binds Mod-Enter, so anything outside the keymap would
   * fire *after* a hard break was inserted into the document. Must be
   * accompanied by visible helper text saying the shortcut exists.
   */
  onSubmitShortcut?: () => void
  /**
   * Ctrl/Cmd+S. Registered in the keymap for the same reason as the submit
   * shortcut, and it returns `true` so the browser's own Save Page dialog never
   * opens over the editor.
   */
  onSaveShortcut?: () => void
  /**
   * Fired the first time (and every time) the document changes.
   *
   * Separate from `onChange`, which serializes the whole document to Markdown
   * on every keystroke. A caller that only needs to know "this has been edited"
   * — the unsaved-changes guard — must not pay for a full serialization per
   * character on a long page.
   */
  onDocChanged?: () => void
  /** Accessible name for the editable region. Defaults to 'Page content'. */
  ariaLabel?: string
  /**
   * id of an element describing the editor (e.g. the composer's
   * keyboard-hint helper text) — contenteditable regions get no automatic
   * label/description association the way TextField wires helperText, so
   * the caller passes the wiring in explicitly.
   */
  ariaDescribedBy?: string
}

/**
 * The shared editor component (design.md §4's "one renderer" rule — this is
 * also what page *view* mode should render through, with `editable: false`,
 * so there is never a second Markdown rendering path to drift out of sync).
 */
export const RichTextEditor = forwardRef<RichTextEditorHandle, RichTextEditorProps>(function RichTextEditor(
  {
    initialMarkdown,
    editable = true,
    onChange,
    showToolbar = true,
    pageId,
    collab,
    onSubmitShortcut,
    onSaveShortcut,
    onDocChanged,
    ariaLabel,
    ariaDescribedBy,
  },
  ref,
) {
  const [uploadError, setUploadError] = useState<string | null>(null)

  // The shortcut extensions are created once (useEditor's extension list is
  // fixed at mount) — the refs keep the latest callbacks reachable without
  // rebuilding the editor, same latest-value-in-a-ref pattern as
  // useCoEditSession's seedMarkdownRef (updated in an effect, not during
  // render).
  const submitShortcutRef = useRef(onSubmitShortcut)
  const saveShortcutRef = useRef(onSaveShortcut)
  const docChangedRef = useRef(onDocChanged)
  useEffect(() => {
    submitShortcutRef.current = onSubmitShortcut
    saveShortcutRef.current = onSaveShortcut
    docChangedRef.current = onDocChanged
  })

  // The collab extension builder rides the binding (see CollabBinding's
  // comment) so this file never statically imports the CRDT chunk.
  const baseExtensions = collab ? collab.extensionsModule.buildCollabExtensions(collab) : richTextExtensions
  const editor = useEditor({
    extensions: [
      ...baseExtensions,
      // priority above StarterKit's HardBreak ('Mod-Enter' inserts a
      // hard break) and CodeBlock ('Mod-Enter' exits the block) — in a
      // composer, submit wins everywhere, matching Ask and search.
      Extension.create({
        name: 'editorShortcuts',
        priority: 1000,
        addKeyboardShortcuts() {
          return {
            'Mod-Enter': () => {
              // Unbound in page mode: falling through leaves HardBreak's own
              // binding intact, which is what Mod-Enter should do there.
              if (!submitShortcutRef.current) return false
              submitShortcutRef.current()
              return true
            },
            // Returning true even when nothing is bound would swallow the
            // browser's Save Page for no reason; returning true when something
            // IS bound is the point, so the native dialog never opens over the
            // editor.
            'Mod-s': () => {
              if (!saveShortcutRef.current) return false
              saveShortcutRef.current()
              return true
            },
          }
        },
      }),
    ],
    // Collaborative mode: content comes from the Y.Doc fragment, never
    // from here — a second content source would duplicate the document.
    ...(collab ? {} : { content: markdownToJson(initialMarkdown) }),
    editable,
    editorProps: {
      attributes: {
        class: 'rw-editor-content',
        // The textbox role belongs to the EDITING surface only.
        //
        // `editorProps.attributes` land on the ProseMirror element whether or
        // not it is editable, so applying these unconditionally made every page
        // VIEW — and every rendered comment, and every help topic — announce as
        // a single flat form field: `role="textbox"` overrides the descendant
        // semantics, so the headings, lists, tables and links inside stop being
        // navigable structures, and the heading anchors this file computes below
        // stop being reachable. axe cannot see it (aria-multiline is permitted
        // on textbox), which is why the read-only assertion in
        // __tests__/readOnlyRendering.test.tsx exists.
        //
        // A read-only render is a document, so it gets a document's markup: no
        // role at all. The name still has somewhere to live when a caller
        // supplies one — `aria-label` on a plain div is inert, so it moves onto
        // a labelled `region` instead, which is a landmark a reader can jump to.
        ...(editable
          ? {
              role: 'textbox',
              'aria-multiline': 'true',
              'aria-label': ariaLabel ?? 'Page content',
              ...(ariaDescribedBy ? { 'aria-describedby': ariaDescribedBy } : {}),
            }
          : ariaLabel
            ? { role: 'region', 'aria-label': ariaLabel }
            : {}),
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
      docChangedRef.current?.()
      onChange?.(jsonToMarkdown(updated.getJSON()))
    },
  })

  useImperativeHandle(
    ref,
    () => ({
      getMarkdown: () => (editor ? jsonToMarkdown(editor.getJSON()) : initialMarkdown),
      // `false` for emitUpdate: clearing after a successful post is not an edit
      // the author made, so it must not mark a composer dirty or fire onChange.
      clear: () => editor?.commands.clearContent(false),
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

  // Read-only rendering is CONTENT, not a control. The outlined frame and the
  // generous padding below are affordances for composing — they say "you may type
  // here" — and applying them to a rendered comment or page body drew a bordered
  // card with 16px of inset around it, which on a one-line comment is mostly empty
  // box. (Its companion, the 200px min-height in editor-content.css, is scoped to
  // the contenteditable surface for the same reason.) Same renderer either way,
  // per this component's own "one renderer" rule — only its chrome differs.
  if (!editable) {
    return <EditorContent editor={editor} />
  }

  return (
    <Paper variant="outlined" sx={{ overflow: 'hidden' }}>
      {showToolbar && <EditorToolbar editor={editor} />}
      {/* Inline, not a snackbar (web/README.md's feedback rule): a failed
          upload is a state the author has to act on — the image they
          dropped is NOT in the document — and an auto-hiding toast can
          expire while they are still typing and never be read. Sits above
          the content, next to the toolbar, so it is on screen regardless of
          how far down the drop landed; re-dropping the file is the retry,
          which is why the message keeps naming it. */}
      {uploadError && (
        <Alert severity="error" square onClose={() => setUploadError(null)}>
          {uploadError}
        </Alert>
      )}
      <Box sx={{ p: 2 }}>
        <EditorContent editor={editor} />
      </Box>
      {editor && <EmojiSuggestionPopup editor={editor} />}
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
    onError(describeAttachmentUnavailable({ kind: 'UPLOAD_FAILED', fileName: file.name }).summary)
  }
}
