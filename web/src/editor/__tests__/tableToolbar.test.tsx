import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, render, screen } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { Editor } from '@tiptap/core'
import { EditorToolbar } from '../EditorToolbar'
import { editorExtensions } from '../extensions'
import { markdownToJson } from '../markdown/fromMarkdown'
import { jsonToMarkdown } from '../markdown/toMarkdown'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { setEmojiRegistry } from '../../emoji/registry'
import { expectNoAxeViolations } from '../../test/axe'

// The emoji picker's previews go through the authenticated blob cache —
// stub it so the axe pass below never touches the network.
vi.mock('../../emoji/emojiBlobCache', () => ({
  getEmojiUrl: () => Promise.resolve('data:image/gif;base64,R0lGODlhAQABAAAAACw='),
  peekEmojiUrl: () => 'data:image/gif;base64,R0lGODlhAQABAAAAACw=',
  resetEmojiBlobCache: () => {},
}))

/**
 * The contextual table toolbar section (design.md §4 phase 1): present only
 * while the selection is inside a table, merge disabled when the selection
 * cannot merge — including the header/body-boundary case, which has no
 * Markdown representation — and alignment applied per column. The §4
 * principle in UI form: never offer what won't survive saving.
 */

const TWO_BY_TWO = '| a | b |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |\n'

let editor: Editor | null = null

afterEach(() => {
  editor?.destroy()
  editor = null
})

function renderToolbar(markdown: string): Editor {
  editor = new Editor({ extensions: editorExtensions, content: markdownToJson(markdown) })
  const mock = createMockUrqlClient((name) => {
    if (name === 'GitLabStatus') return { gitlabStatus: { configured: false, baseUrl: null, viewerHasToken: false } }
    return undefined
  })
  render(
    <UrqlProvider value={mock.client}>
      <EditorToolbar editor={editor} />
    </UrqlProvider>,
  )
  return editor
}

function cellPositions(e: Editor): number[] {
  const positions: number[] = []
  e.state.doc.descendants((node, pos) => {
    if (node.type.name === 'tableCell' || node.type.name === 'tableHeader') positions.push(pos)
    return true
  })
  return positions
}

describe('editor toolbar — axe pass (WCAG 2.2 AA policy, test/axe.ts)', () => {
  it('has no axe violations with table controls showing and the emoji picker open', async () => {
    setEmojiRegistry([
      { name: 'rocket', etag: '"r1"' },
      { name: 'banana', etag: '"b1"' },
    ])
    renderToolbar(TWO_BY_TWO)
    screen.getByRole('group', { name: 'Table cell controls' })
    await expectNoAxeViolations()
    fireEvent.click(screen.getByRole('button', { name: 'Insert emoji' }))
    await screen.findByRole('listbox', { name: 'Custom emojis' })
    await expectNoAxeViolations()
    setEmojiRegistry([])
  })
})

describe('contextual table controls', () => {
  it('are absent while the selection is outside any table', () => {
    renderToolbar('plain paragraph\n')
    expect(screen.queryByRole('group', { name: 'Table cell controls' })).not.toBeInTheDocument()
  })

  it('appear when the selection is inside a table (and merge/split are disabled for a lone caret)', () => {
    renderToolbar(TWO_BY_TWO) // initial selection lands in the first header cell
    expect(screen.getByRole('group', { name: 'Table cell controls' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Merge cells' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Split cell' })).toBeDisabled()
  })

  it('merge is disabled when the selection crosses the header/body boundary, enabled within one section', () => {
    const e = renderToolbar(TWO_BY_TWO)
    const cells = cellPositions(e)

    act(() => {
      e.commands.setCellSelection({ anchorCell: cells[0], headCell: cells[2] }) // header "a" → body "1"
    })
    expect(screen.getByRole('button', { name: 'Merge cells' })).toBeDisabled()

    act(() => {
      e.commands.setCellSelection({ anchorCell: cells[2], headCell: cells[3] }) // body "1" → body "2"
    })
    const merge = screen.getByRole('button', { name: 'Merge cells' })
    expect(merge).toBeEnabled()

    act(() => {
      fireEvent.click(merge)
    })
    expect(jsonToMarkdown(e.getJSON())).toBe('| a | b |\n| --- | --- |\n| 1<br>2 ||\n| 3 | 4 |\n')
    // …and the merged cell can be split again from the toolbar
    const split = screen.getByRole('button', { name: 'Split cell' })
    expect(split).toBeEnabled()
  })

  it('the alignment toggles set the whole column and reflect the current cell alignment', () => {
    const e = renderToolbar(TWO_BY_TWO) // caret in header cell "a" (column 0)

    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Align column center' }))
    })
    expect(jsonToMarkdown(e.getJSON())).toBe('| a | b |\n| :---: | --- |\n| 1 | 2 |\n| 3 | 4 |\n')
    expect(screen.getByRole('button', { name: 'Align column center' })).toHaveAttribute('aria-pressed', 'true')

    // Clicking the active toggle clears back to the unaligned column.
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Align column center' }))
    })
    expect(jsonToMarkdown(e.getJSON())).toBe(TWO_BY_TWO)
    expect(screen.getByRole('button', { name: 'Align column center' })).toHaveAttribute('aria-pressed', 'false')
  })
})
