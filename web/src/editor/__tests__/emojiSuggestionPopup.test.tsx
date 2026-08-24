import { describe, expect, it, vi } from 'vitest'
import { render } from '@testing-library/react'
import type { Editor } from '@tiptap/core'
import { EmojiSuggestionPopup } from '../emoji/EmojiSuggestionPopup'
import { setEmojiRegistry } from '../../emoji/registry'
import type { EmojiSuggestionState } from '../emoji/EmojiSuggestion'

vi.mock('../../emoji/emojiBlobCache', () => ({
  getEmojiUrl: () => Promise.resolve('data:image/gif;base64,R0lGODlhAQABAAAAACw='),
  peekEmojiUrl: () => 'data:image/gif;base64,R0lGODlhAQABAAAAACw=',
  resetEmojiBlobCache: () => {},
}))

/**
 * WCAG 4.1.2 combobox pattern for the `:` autocomplete: DOM focus stays in
 * the contenteditable while ArrowUp/Down move the highlight, so the popup
 * must expose the highlight to assistive tech via aria-activedescendant +
 * aria-controls on the editor DOM, pointing at real option ids. The popup
 * only touches `on/off`, storage, `isDestroyed`, `isFocused`, and
 * `view.coordsAtPos`/`view.dom`, so a stub editor drives it deterministically
 * (jsdom can't focus a ProseMirror contenteditable for real).
 */
function stubEditor(state: EmojiSuggestionState, { focused = true } = {}) {
  const dom = document.createElement('div')
  document.body.appendChild(dom)
  const editor = {
    on: () => {},
    off: () => {},
    storage: { rwEmojiSuggestion: state },
    isDestroyed: false,
    isFocused: focused,
    view: {
      dom,
      coordsAtPos: () => ({ left: 0, right: 0, top: 0, bottom: 10 }),
    },
  } as unknown as Editor
  return { editor, dom }
}

const activeState = (index: number): EmojiSuggestionState => ({
  active: true,
  query: 'ro',
  from: 1,
  to: 4,
  items: ['rocket', 'rock'],
  index,
})

describe('EmojiSuggestionPopup — aria-activedescendant wiring', () => {
  it('points the editor DOM at the highlighted option while open', async () => {
    setEmojiRegistry([
      { name: 'rocket', etag: '"r1"' },
      { name: 'rock', etag: '"r2"' },
    ])
    const { editor, dom } = stubEditor(activeState(1))
    const { container } = render(<EmojiSuggestionPopup editor={editor} />)

    const listbox = document.body.querySelector('[role="listbox"]')
    expect(listbox).not.toBeNull()
    const listboxId = listbox!.getAttribute('id')
    expect(listboxId).toBeTruthy()
    expect(dom.getAttribute('aria-controls')).toBe(listboxId)

    const activeId = dom.getAttribute('aria-activedescendant')
    expect(activeId).toBe(`${listboxId}-option-1`)
    const activeOption = document.getElementById(activeId!)
    expect(activeOption).not.toBeNull()
    expect(activeOption!.getAttribute('role')).toBe('option')
    expect(activeOption!.getAttribute('aria-selected')).toBe('true')
    expect(activeOption!.textContent).toContain(':rock:')
    expect(container).toBeDefined()
    setEmojiRegistry([])
  })

  it('removes the attributes once the popup closes (blur/dismiss)', () => {
    setEmojiRegistry([{ name: 'rocket', etag: '"r1"' }])
    const { editor, dom } = stubEditor(activeState(0))
    const { rerender } = render(<EmojiSuggestionPopup editor={editor} />)
    expect(dom.getAttribute('aria-activedescendant')).not.toBeNull()

    ;(editor as unknown as { isFocused: boolean }).isFocused = false
    rerender(<EmojiSuggestionPopup editor={editor} />)
    expect(dom.getAttribute('aria-activedescendant')).toBeNull()
    expect(dom.getAttribute('aria-controls')).toBeNull()
    setEmojiRegistry([])
  })
})
