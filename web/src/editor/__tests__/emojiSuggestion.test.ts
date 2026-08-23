import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { EmojiSuggestion, acceptEmojiSuggestion, type EmojiSuggestionState } from '../emoji/EmojiSuggestion'
import { filterEmojiNames, matchEmojiInput } from '../emoji/emojiSuggestionMatch'
import { resetEmojiRegistry, setEmojiRegistry } from '../../emoji/registry'

/**
 * The `:` autocomplete (design.md §19). The pure matcher/filter carry the
 * logic; the Editor-level tests prove the extension wires them to real
 * typing state and that acceptance inserts **literal `:name:` text** —
 * nothing new for the serializer, by construction.
 */

describe('matchEmojiInput — when is the author asking for an emoji?', () => {
  it('triggers on a trailing :partial at block start and after whitespace', () => {
    expect(matchEmojiInput(':ro')).toEqual({ query: 'ro', length: 3 })
    expect(matchEmojiInput('see :ro')).toEqual({ query: 'ro', length: 3 })
  })

  it('does not trigger mid-token: a colon after a name character is prose (10:3, key:value)', () => {
    expect(matchEmojiInput('10:3')).toBeNull()
    expect(matchEmojiInput('key:value')).toBeNull()
  })

  it('triggers right after a completed emoji (adjacent emojis)', () => {
    expect(matchEmojiInput(':tada::ro')).toEqual({ query: 'ro', length: 3 })
  })

  it('requires at least one query character — a bare colon is punctuation', () => {
    expect(matchEmojiInput(':')).toBeNull()
    expect(matchEmojiInput('wait: ')).toBeNull()
  })

  it('rejects characters outside the name grammar', () => {
    expect(matchEmojiInput(':Ro')).toBeNull()
    expect(matchEmojiInput(':ro ')).toBeNull()
  })
})

describe('filterEmojiNames — prefix filter for the popup', () => {
  it('filters by prefix, sorted, capped', () => {
    expect(filterEmojiNames(['rocket', 'rat', 'rock', 'tada'], 'ro')).toEqual(['rock', 'rocket'])
    expect(filterEmojiNames(['b', 'a', 'c'], '')).toEqual(['a', 'b', 'c'])
    const many = Array.from({ length: 20 }, (_, i) => `e${String(i).padStart(2, '0')}`)
    expect(filterEmojiNames(many, 'e')).toHaveLength(8)
  })
})

describe('EmojiSuggestion extension — against a real editor', () => {
  let editor: Editor

  const storage = () => editor.storage.rwEmojiSuggestion as EmojiSuggestionState

  const pressKey = (key: string) => {
    let handled = false
    editor.view.someProp('handleKeyDown', (f) => {
      const result = f(editor.view, new KeyboardEvent('keydown', { key }))
      if (result === true) handled = true
      return result
    })
    return handled
  }

  beforeEach(() => {
    setEmojiRegistry([
      { name: 'rocket', etag: 'e1' },
      { name: 'rock', etag: 'e2' },
      { name: 'tada', etag: 'e3' },
    ])
    editor = new Editor({ extensions: [StarterKit, EmojiSuggestion], content: '<p></p>' })
  })

  afterEach(() => {
    editor.destroy()
    resetEmojiRegistry()
  })

  it('activates with prefix-filtered items while typing :partial', () => {
    editor.commands.insertContent('Launch :ro')
    expect(storage().active).toBe(true)
    expect(storage().query).toBe('ro')
    expect(storage().items).toEqual(['rock', 'rocket'])
    expect(storage().index).toBe(0)
  })

  it('stays quiet for prose colons like clock times', () => {
    editor.commands.insertContent('meet at 10:3')
    expect(storage().active).toBe(false)
  })

  it('stays quiet when nothing in the registry matches the prefix', () => {
    editor.commands.insertContent(':zz')
    expect(storage().active).toBe(false)
    expect(storage().items).toEqual([])
  })

  it('ArrowDown/ArrowUp move the highlight and swallow the key while active', () => {
    editor.commands.insertContent(':ro')
    expect(pressKey('ArrowDown')).toBe(true)
    expect(storage().index).toBe(1)
    expect(pressKey('ArrowUp')).toBe(true)
    expect(storage().index).toBe(0)
  })

  it('Enter accepts the highlighted item, inserting the literal :name: text', () => {
    editor.commands.insertContent('Launch :ro')
    expect(pressKey('ArrowDown')).toBe(true) // highlight "rocket"
    expect(pressKey('Enter')).toBe(true)
    expect(editor.state.doc.textContent).toBe('Launch :rocket:')
    expect(storage().active).toBe(false) // the closing colon ends the trigger
  })

  it('acceptEmojiSuggestion (the popup click path) replaces the typed partial with literal text', () => {
    editor.commands.insertContent(':ta')
    expect(storage().active).toBe(true)
    acceptEmojiSuggestion(editor, 'tada')
    expect(editor.state.doc.textContent).toBe(':tada:')
  })

  it('Escape dismisses until the trigger moves, and Enter falls through to the default keymap', () => {
    editor.commands.insertContent(':ro')
    expect(pressKey('Escape')).toBe(true)
    expect(storage().active).toBe(false)
    // Enter now reaches the base keymap: it splits the paragraph instead of
    // inserting an emoji — the typed partial stays literal.
    pressKey('Enter')
    expect(editor.state.doc.textContent).toBe(':ro')
    expect(editor.state.doc.childCount).toBe(2)
    // A new trigger elsewhere re-activates.
    editor.commands.insertContent(':ta')
    expect(storage().active).toBe(true)
    expect(storage().query).toBe('ta')
  })

  it('deactivates when the editor becomes read-only', () => {
    editor.commands.insertContent(':ro')
    expect(storage().active).toBe(true)
    editor.setEditable(false)
    editor.view.dispatch(editor.state.tr.setMeta('noop', true))
    expect(storage().active).toBe(false)
  })
})
