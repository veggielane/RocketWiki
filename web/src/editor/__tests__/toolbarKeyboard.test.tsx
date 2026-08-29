import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { RichTextEditor } from '../RichTextEditor'
import { createMockUrqlClient } from '../../test/mockUrqlClient'

vi.mock('../../pages/pageContext', () => ({ useCurrentPageId: () => null }))

/**
 * `role="toolbar"` is a promise about the keyboard, and the toolbar was making
 * it without keeping it: every one of its ~20 controls was an independent tab
 * stop, so a keyboard user tabbing toward the editing surface had to pass
 * through all of them — more once a table selection revealed the contextual
 * group.
 *
 * The WAI-ARIA practices say a toolbar is ONE tab stop with arrow keys moving
 * between the controls inside. axe cannot check that: the role is correct, the
 * labels are correct, and the behaviour it implies is simply absent.
 */

function renderToolbar() {
  const mock = createMockUrqlClient((name) =>
    name === 'GitLabStatus' ? { gitlabStatus: { configured: false, baseUrl: null, viewerHasToken: false } } : undefined,
  )
  render(
    <UrqlProvider value={mock.client}>
      <RichTextEditor initialMarkdown="Hello.\n" />
    </UrqlProvider>,
  )
  return screen.getByRole('toolbar', { name: 'Formatting' })
}

function toolbarButtons(toolbar: HTMLElement) {
  return Array.from(toolbar.querySelectorAll<HTMLButtonElement>('button:not([disabled])'))
}

describe('editor toolbar keyboard contract', () => {
  it('is a single tab stop', async () => {
    const toolbar = renderToolbar()
    const buttons = toolbarButtons(toolbar)
    expect(buttons.length).toBeGreaterThan(5)

    // Exactly one control is reachable by Tab; the rest are reached with the
    // arrow keys, which is what the toolbar role means.
    const tabbable = buttons.filter((b) => b.tabIndex === 0)
    expect(tabbable).toHaveLength(1)
    expect(tabbable[0]).toBe(buttons[0])
  })

  it('moves between controls with the arrow keys, and wraps', () => {
    const toolbar = renderToolbar()
    const buttons = toolbarButtons(toolbar)

    buttons[0]!.focus()
    fireEvent.keyDown(toolbar, { key: 'ArrowRight' })
    expect(document.activeElement).toBe(buttons[1])

    fireEvent.keyDown(toolbar, { key: 'ArrowLeft' })
    expect(document.activeElement).toBe(buttons[0])

    // A ring, not a dead stop — arrowing off the end reads as a broken key.
    fireEvent.keyDown(toolbar, { key: 'ArrowLeft' })
    expect(document.activeElement).toBe(buttons[buttons.length - 1])
  })

  it('jumps to the ends with Home and End', () => {
    const toolbar = renderToolbar()
    const buttons = toolbarButtons(toolbar)

    buttons[2]!.focus()
    fireEvent.keyDown(toolbar, { key: 'End' })
    expect(document.activeElement).toBe(buttons[buttons.length - 1])

    fireEvent.keyDown(toolbar, { key: 'Home' })
    expect(document.activeElement).toBe(buttons[0])
  })

  it('remembers where you were, so Tab returns to the control you last used', () => {
    const toolbar = renderToolbar()
    const buttons = toolbarButtons(toolbar)

    // Focus lands on a control directly (a click, or arrowing to it) — that
    // one becomes the toolbar's tab stop rather than the first button.
    fireEvent.focusIn(buttons[3]!, { target: buttons[3] })
    expect(buttons[3]!.tabIndex).toBe(0)
    expect(buttons.filter((b) => b.tabIndex === 0)).toHaveLength(1)
  })
})
