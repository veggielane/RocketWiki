import { useEffect, useState } from 'react'
import type { Editor } from '@tiptap/core'
import { MenuItem, MenuList, Paper, Popper } from '@mui/material'
import { getEmojiRegistry } from '../../emoji/registry'
import { EmojiImg } from '../../emoji/EmojiImg'
import { acceptEmojiSuggestion, type EmojiSuggestionState } from './EmojiSuggestion'

/**
 * The visual half of the `:` autocomplete: renders the EmojiSuggestion
 * extension's per-editor storage as a caret-anchored MUI Popper. Purely
 * presentational — filtering, highlight index, and acceptance live in the
 * extension so the keyboard path works even if this never mounts.
 * Selecting an item (keyboard or click) inserts the literal `:name:` text.
 */
export function EmojiSuggestionPopup({ editor }: { editor: Editor }) {
  const [, setTick] = useState(0)

  useEffect(() => {
    const repaint = () => setTick((t) => t + 1)
    editor.on('transaction', repaint)
    editor.on('focus', repaint)
    editor.on('blur', repaint)
    return () => {
      editor.off('transaction', repaint)
      editor.off('focus', repaint)
      editor.off('blur', repaint)
    }
  }, [editor])

  const state = editor.storage.rwEmojiSuggestion as EmojiSuggestionState | undefined
  if (!state?.active || state.items.length === 0 || editor.isDestroyed || !editor.isFocused) {
    return null
  }

  let anchorRect: DOMRect
  try {
    const coords = editor.view.coordsAtPos(state.from)
    anchorRect = new DOMRect(coords.left, coords.top, 0, coords.bottom - coords.top)
  } catch {
    return null // stale position mid-transaction — next repaint recovers
  }

  const registry = getEmojiRegistry()

  return (
    <Popper
      open
      anchorEl={{ getBoundingClientRect: () => anchorRect }}
      placement="bottom-start"
      sx={{ zIndex: (theme) => theme.zIndex.modal }}
    >
      <Paper elevation={4}>
        <MenuList dense role="listbox" aria-label="Emoji suggestions">
          {state.items.map((name, i) => (
            <MenuItem
              key={name}
              role="option"
              aria-selected={i === state.index}
              selected={i === state.index}
              // preventDefault keeps the editor focused so the click can
              // insert at the tracked range instead of racing a blur.
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => acceptEmojiSuggestion(editor, name)}
            >
              <EmojiImg name={name} etag={registry.get(name) ?? ''} size={18} />
              <span style={{ marginLeft: 8 }}>{`:${name}:`}</span>
            </MenuItem>
          ))}
        </MenuList>
      </Paper>
    </Popper>
  )
}
