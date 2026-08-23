import { Extension, type Editor } from '@tiptap/core'
import type { EditorState } from '@tiptap/pm/state'
import { getEmojiRegistry } from '../../emoji/registry'
import { filterEmojiNames, matchEmojiInput } from './emojiSuggestionMatch'

/**
 * `:` autocomplete for custom emojis (design.md §19). No mention-style
 * suggestion plumbing existed to reuse (mentions arrive via Markdown, not
 * an editor popup) and `@tiptap/suggestion` isn't a dependency, so this is
 * a small self-contained extension: it tracks a trailing `:partial` before
 * the caret in per-editor `storage` (each comment box gets its own state),
 * and the React popup (EmojiSuggestionPopup.tsx) renders from that storage
 * on the editor's `transaction` events.
 *
 * **Accepting a suggestion just types the literal `:name:` text** — no
 * node, no mark, nothing new to serialize; the round-trip is untouched by
 * construction, same reasoning as the decoration renderer.
 *
 * Keyboard: Arrow Up/Down move the highlight, Enter/Tab accept, Escape
 * dismisses (until the trigger position changes). Registered at high
 * priority so an open popup's Enter wins over the paragraph-split keymap;
 * every handler returns false the moment the popup is inactive, so typing
 * is untouched otherwise.
 */

export interface EmojiSuggestionState {
  active: boolean
  query: string
  /** Document position of the opening `:`. */
  from: number
  /** Caret position (end of the typed partial). */
  to: number
  items: string[]
  index: number
}

interface EmojiSuggestionStorage extends EmojiSuggestionState {
  dismissedFrom: number | null
}

// TipTap's typed per-editor storage — makes `editor.storage.rwEmojiSuggestion`
// well-typed everywhere (the popup reads it too).
declare module '@tiptap/core' {
  interface Storage {
    rwEmojiSuggestion: EmojiSuggestionStorage
  }
}

const inactive = { active: false, query: '', from: 0, to: 0, items: [], index: 0 } satisfies EmojiSuggestionState

function computeMatch(state: EditorState): { from: number; to: number; query: string } | null {
  const { selection } = state
  if (!selection.empty) return null
  const { $from } = selection
  if (!$from.parent.isTextblock || $from.parent.type.spec.code) return null
  const textBefore = $from.parent.textBetween(0, $from.parentOffset, undefined, '￼')
  const match = matchEmojiInput(textBefore)
  if (!match) return null
  return { from: $from.pos - match.length, to: $from.pos, query: match.query }
}

/** Replaces the typed `:partial` with the literal `:name:` text. Shared by Enter/Tab and popup clicks. */
export function acceptEmojiSuggestion(editor: Editor, name: string): void {
  const storage = editor.storage.rwEmojiSuggestion as EmojiSuggestionStorage
  if (!storage.active) return
  const { from, to } = storage
  editor
    .chain()
    .focus()
    .command(({ tr }) => {
      tr.insertText(`:${name}:`, from, to)
      return true
    })
    .run()
}

export const EmojiSuggestion = Extension.create({
  name: 'rwEmojiSuggestion',
  priority: 1000,

  addStorage(): EmojiSuggestionStorage {
    return { ...inactive, items: [], dismissedFrom: null }
  },

  onTransaction() {
    const storage = this.storage as EmojiSuggestionStorage
    if (!this.editor.isEditable) {
      Object.assign(storage, inactive, { items: [] })
      return
    }
    const match = computeMatch(this.editor.state)
    if (!match) {
      Object.assign(storage, inactive, { items: [] })
      storage.dismissedFrom = null
      return
    }
    if (storage.dismissedFrom !== null && storage.dismissedFrom !== match.from) {
      storage.dismissedFrom = null // moved to a new trigger — undismiss
    }
    const items = storage.dismissedFrom === match.from ? [] : filterEmojiNames(getEmojiRegistry().keys(), match.query)
    const queryChanged = match.query !== storage.query || match.from !== storage.from
    Object.assign(storage, {
      active: storage.dismissedFrom !== match.from && items.length > 0,
      query: match.query,
      from: match.from,
      to: match.to,
      items,
      index: queryChanged ? 0 : Math.min(storage.index, Math.max(items.length - 1, 0)),
    })
  },

  addKeyboardShortcuts() {
    const rerender = (editor: Editor) => {
      // Highlight moves aren't document changes; a meta-only transaction
      // makes the popup (which renders on `transaction`) repaint.
      editor.view.dispatch(editor.state.tr.setMeta('rwEmojiSuggestionNav', true))
    }
    const storageOf = (editor: Editor) => editor.storage.rwEmojiSuggestion as EmojiSuggestionStorage
    return {
      ArrowDown: ({ editor }) => {
        const storage = storageOf(editor)
        if (!storage.active || storage.items.length === 0) return false
        storage.index = (storage.index + 1) % storage.items.length
        rerender(editor)
        return true
      },
      ArrowUp: ({ editor }) => {
        const storage = storageOf(editor)
        if (!storage.active || storage.items.length === 0) return false
        storage.index = (storage.index - 1 + storage.items.length) % storage.items.length
        rerender(editor)
        return true
      },
      Enter: ({ editor }) => {
        const storage = storageOf(editor)
        if (!storage.active || storage.items.length === 0) return false
        acceptEmojiSuggestion(editor, storage.items[storage.index])
        return true
      },
      Tab: ({ editor }) => {
        const storage = storageOf(editor)
        if (!storage.active || storage.items.length === 0) return false
        acceptEmojiSuggestion(editor, storage.items[storage.index])
        return true
      },
      Escape: ({ editor }) => {
        const storage = storageOf(editor)
        if (!storage.active) return false
        storage.dismissedFrom = storage.from
        storage.active = false
        storage.items = []
        rerender(editor)
        return true
      },
    }
  },
})
