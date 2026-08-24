import { describe, expect, it, vi } from 'vitest'
import { createRef } from 'react'
import { fireEvent, render, screen } from '@testing-library/react'
import { RichTextEditor, type RichTextEditorHandle } from '../RichTextEditor'

/**
 * The composer submit shortcut (comments — see comments/Comments.tsx).
 * Registered as a TipTap keyboard shortcut with priority over StarterKit's
 * HardBreak, because 'Mod-Enter' otherwise inserts a hard break: the test
 * pins BOTH that the callback fires and that the document is untouched —
 * a submit that also mutates the body would post stray trailing breaks.
 */
describe('RichTextEditor — onSubmitShortcut (Ctrl/Cmd+Enter)', () => {
  function renderComposer() {
    const onSubmit = vi.fn()
    const handle = createRef<RichTextEditorHandle>()
    render(
      <RichTextEditor
        ref={handle}
        initialMarkdown="Draft text."
        showToolbar={false}
        ariaLabel="New comment"
        onSubmitShortcut={onSubmit}
      />,
    )
    return { onSubmit, handle, textbox: screen.getByRole('textbox', { name: 'New comment' }) }
  }

  it('Ctrl+Enter fires the callback and leaves the document unchanged (no hard break)', () => {
    const { onSubmit, handle, textbox } = renderComposer()
    fireEvent.keyDown(textbox, { key: 'Enter', ctrlKey: true })
    expect(onSubmit).toHaveBeenCalledOnce()
    expect(handle.current!.getMarkdown()).toBe('Draft text.\n')
  })

  // No Cmd+Enter case: 'Mod' resolves to Cmd only on a Mac platform, and
  // prosemirror-keymap reads navigator.platform once at module load — it
  // cannot be stubbed per-test, and the mapping is upstream behavior anyway.

  it('plain Enter does NOT submit — it stays the paragraph key', () => {
    const { onSubmit, textbox } = renderComposer()
    fireEvent.keyDown(textbox, { key: 'Enter' })
    expect(onSubmit).not.toHaveBeenCalled()
  })

  it('without the prop, Mod-Enter falls through to the stock editor behavior (no crash, no callback machinery)', () => {
    const handle = createRef<RichTextEditorHandle>()
    render(<RichTextEditor ref={handle} initialMarkdown="Page body." showToolbar={false} />)
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'Page content' }), { key: 'Enter', ctrlKey: true })
    // StarterKit's HardBreak owns the key here: a break ('  \n') lands in
    // the doc (at wherever the selection sits — position is not the point).
    expect(handle.current!.getMarkdown()).toContain('  \n')
  })
})
