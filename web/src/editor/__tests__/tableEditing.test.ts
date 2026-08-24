import { afterEach, describe, expect, it } from 'vitest'
import { Editor } from '@tiptap/core'
import { editorExtensions } from '../extensions'
import { markdownToJson } from '../markdown/fromMarkdown'
import { jsonToMarkdown } from '../markdown/toMarkdown'
import { mergeWouldCrossHeaderBoundary } from '../tableEditing'

/**
 * The table EDITING affordances (design.md §4 phase 1): merge/split,
 * per-column alignment, the header-boundary merge guard, and
 * Enter-inserts-a-line-break — all driven through a real Editor so the
 * commands are tested against the schema and keymap users actually get.
 */

let editor: Editor | null = null

afterEach(() => {
  editor?.destroy()
  editor = null
})

function editorWith(markdown: string): Editor {
  editor = new Editor({ extensions: editorExtensions, content: markdownToJson(markdown) })
  return editor
}

/** Positions (pos BEFORE the cell node) of every cell, in document order. */
function cellPositions(e: Editor): number[] {
  const positions: number[] = []
  e.state.doc.descendants((node, pos) => {
    if (node.type.name === 'tableCell' || node.type.name === 'tableHeader') positions.push(pos)
    return true
  })
  return positions
}

const TWO_BY_TWO = '| a | b |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |\n'

describe('Enter inside a table cell', () => {
  it('inserts a line break (a hardBreak → `<br>`), not a second paragraph', () => {
    const e = editorWith('| a |\n| --- |\n| body |\n')
    const bodyCell = cellPositions(e)[1]
    e.commands.setTextSelection(bodyCell + 2 + 'body'.length) // end of the cell's text
    expect(e.commands.keyboardShortcut('Enter')).toBe(true)
    e.commands.insertContent('more')
    expect(jsonToMarkdown(e.getJSON())).toBe('| a |\n| --- |\n| body<br>more |\n')
  })

  it('control: Enter in an ordinary paragraph still splits into two paragraphs', () => {
    const e = editorWith('hello\n')
    e.commands.setTextSelection(6) // end of "hello"
    expect(e.commands.keyboardShortcut('Enter')).toBe(true)
    e.commands.insertContent('world')
    expect(jsonToMarkdown(e.getJSON())).toBe('hello\n\nworld\n')
  })
})

describe('setTableColumnAlign', () => {
  it('aligns the whole column the cursor is in — header cell included — and serializes as the delimiter', () => {
    const e = editorWith(TWO_BY_TWO)
    const secondBodyCell = cellPositions(e)[3] // row 1, column 1 ("2")
    e.commands.setTextSelection(secondBodyCell + 2)
    expect(e.commands.setTableColumnAlign('center')).toBe(true)
    expect(jsonToMarkdown(e.getJSON())).toBe('| a | b |\n| --- | :---: |\n| 1 | 2 |\n| 3 | 4 |\n')
    // every cell in column 1 carries the attr (rendering is per-cell)
    const aligns: (string | null)[] = []
    e.state.doc.descendants((node) => {
      if (node.type.name === 'tableCell' || node.type.name === 'tableHeader') aligns.push(node.attrs.align)
      return true
    })
    expect(aligns).toEqual([null, 'center', null, 'center', null, 'center'])
  })

  it('null clears back to the unaligned `---` delimiter', () => {
    const e = editorWith('| a | b |\n| :--- | --- |\n| 1 | 2 |\n')
    const firstBodyCell = cellPositions(e)[2]
    e.commands.setTextSelection(firstBodyCell + 2)
    expect(e.commands.setTableColumnAlign(null)).toBe(true)
    expect(jsonToMarkdown(e.getJSON())).toBe('| a | b |\n| --- | --- |\n| 1 | 2 |\n')
  })

  it('a spanning cell that intersects the column is aligned too', () => {
    const e = editorWith('| a | b |\n| --- | --- |\n| wide ||\n| 1 | 2 |\n')
    const cell = cellPositions(e)[4] // "2", column 1
    e.commands.setTextSelection(cell + 2)
    expect(e.commands.setTableColumnAlign('right')).toBe(true)
    // "wide" spans into column 1, so it aligns as well (a column is a
    // visual unit); the delimiter for column 0 comes from its own anchors.
    expect(jsonToMarkdown(e.getJSON())).toBe('| a | b |\n| --- | ---: |\n| wide ||\n| 1 | 2 |\n')
  })

  it('returns false outside a table', () => {
    const e = editorWith('just text\n')
    expect(e.commands.setTableColumnAlign('left')).toBe(false)
  })
})

describe('merge and split', () => {
  it('merging two body cells produces the colspan form, splitting restores plain cells', () => {
    const e = editorWith(TWO_BY_TWO)
    const cells = cellPositions(e)
    e.commands.setCellSelection({ anchorCell: cells[2], headCell: cells[3] }) // "1" + "2"
    expect(e.commands.mergeTableCells()).toBe(true)
    expect(jsonToMarkdown(e.getJSON())).toBe('| a | b |\n| --- | --- |\n| 1<br>2 ||\n| 3 | 4 |\n')
    expect(e.commands.splitCell()).toBe(true)
    expect(jsonToMarkdown(e.getJSON())).toBe('| a | b |\n| --- | --- |\n| 1<br>2 |  |\n| 3 | 4 |\n')
  })

  it('merging vertically produces the ^^ rowspan form', () => {
    const e = editorWith(TWO_BY_TWO)
    const cells = cellPositions(e)
    e.commands.setCellSelection({ anchorCell: cells[2], headCell: cells[4] }) // "1" above "3"
    expect(e.commands.mergeTableCells()).toBe(true)
    expect(jsonToMarkdown(e.getJSON())).toBe('| a | b |\n| --- | --- |\n| 1<br>3 | 2 |\n| ^^ | 4 |\n')
  })

  it('a merge spanning the header/body boundary is refused (no Markdown representation)', () => {
    const e = editorWith(TWO_BY_TWO)
    const cells = cellPositions(e)
    const before = jsonToMarkdown(e.getJSON())
    e.commands.setCellSelection({ anchorCell: cells[0], headCell: cells[2] }) // header "a" + body "1"
    expect(mergeWouldCrossHeaderBoundary(e.state)).toBe(true)
    expect(e.commands.mergeTableCells()).toBe(false)
    expect(jsonToMarkdown(e.getJSON())).toBe(before)
  })

  it('merging inside the header row alone is allowed (header colspan has a representation)', () => {
    const e = editorWith(TWO_BY_TWO)
    const cells = cellPositions(e)
    e.commands.setCellSelection({ anchorCell: cells[0], headCell: cells[1] })
    expect(mergeWouldCrossHeaderBoundary(e.state)).toBe(false)
    expect(e.commands.mergeTableCells()).toBe(true)
    expect(jsonToMarkdown(e.getJSON())).toBe('| a<br>b ||\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |\n')
  })

  it('read mode renders spans and alignment faithfully (same renderer, editable: false)', () => {
    const element = document.createElement('div')
    document.body.appendChild(element)
    const e = new Editor({
      element,
      editable: false,
      extensions: editorExtensions,
      content: markdownToJson('| Stage | Result | Notes |\n| :--- | :---: | ---: |\n| Coast || **hold** |\n| ^^ || 3 |\n'),
    })
    try {
      const spanned = element.querySelector('td[colspan="2"][rowspan="2"]')
      expect(spanned).not.toBeNull()
      expect((spanned as HTMLTableCellElement).style.textAlign).toBe('left')
      const headers = Array.from(element.querySelectorAll('th')) as HTMLTableCellElement[]
      expect(headers.map((th) => th.style.textAlign)).toEqual(['left', 'center', 'right'])
      const lastCell = Array.from(element.querySelectorAll('td')).at(-1) as HTMLTableCellElement
      expect(lastCell.style.textAlign).toBe('right')
    } finally {
      e.destroy()
      element.remove()
    }
  })

  it('everything a merge/align session produces still round-trips byte-identically', () => {
    const e = editorWith(TWO_BY_TWO)
    const cells = cellPositions(e)
    e.commands.setCellSelection({ anchorCell: cells[2], headCell: cells[3] })
    e.commands.mergeTableCells()
    e.commands.setTableColumnAlign('center')
    const markdown = jsonToMarkdown(e.getJSON())
    const reloaded = new Editor({ extensions: editorExtensions, content: markdownToJson(markdown) })
    try {
      expect(jsonToMarkdown(reloaded.getJSON())).toBe(markdown)
    } finally {
      reloaded.destroy()
    }
  })
})
