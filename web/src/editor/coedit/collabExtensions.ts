import Collaboration from '@tiptap/extension-collaboration'
import { CollaborationCaret } from '@tiptap/extension-collaboration-caret'
import type { AnyExtension } from '@tiptap/core'
import { renderCaret } from './caretRender'
import { richTextExtensions } from '../richTextExtensions'
import type { CollabBinding } from '../RichTextEditor'

/**
 * The ONLY static importer of the CRDT-side TipTap extensions
 * (Collaboration → y-prosemirror → yjs; CollaborationCaret). This module is
 * reached exclusively via dynamic `import()` — useCoEditSession loads it
 * alongside the provider and hands it to RichTextEditor on the
 * CollabBinding — for the same air-gap-motivated splitting rationale as
 * app/router.tsx: page VIEW routes render through RichTextEditor too (the
 * one-renderer rule), and before this split every read-only page view
 * downloaded the multi-hundred-KB Yjs/co-edit chunk it could never use.
 * Adding a static import of this file to any component would silently undo
 * that.
 *
 * Collaborative variant of the extension list: same schema (the swap rule
 * in richTextExtensions.ts — schema is `editorExtensions`' alone), plus
 * the Yjs binding. `undoRedo: false` because Collaboration replaces
 * prosemirror-history with the Yjs undo manager — two undo stacks over one
 * document would fight (and Collaboration provides its own undo/redo
 * commands + keybindings, so the editor keeps working undo).
 */
export function buildCollabExtensions(collab: CollabBinding): AnyExtension[] {
  return [
    ...richTextExtensions.map((ext) => (ext.name === 'starterKit' ? ext.configure({ undoRedo: false }) : ext)),
    Collaboration.configure({ document: collab.doc }),
    CollaborationCaret.configure({ provider: collab.provider, user: collab.user, render: renderCaret }),
  ]
}
