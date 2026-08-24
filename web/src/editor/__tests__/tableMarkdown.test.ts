import { describe, expect, it } from 'vitest'
import { Editor, getSchema, type JSONContent } from '@tiptap/core'
import { TableMap } from '@tiptap/pm/tables'
import { editorExtensions } from '../extensions'
import { markdownToJson } from '../markdown/fromMarkdown'
import { jsonToMarkdown } from '../markdown/toMarkdown'

/**
 * Table serializer/parser edge rules (design.md §4 phase 1: alignment,
 * spans, `<br>` newlines). The round-trip corpus proves parser-produced
 * documents are byte-stable; these tests pin the rules for documents the
 * EDITOR produces (typed pipes, typed `^^`, mixed alignment, merged cells)
 * and the failure modes that must stay loud.
 */

function roundTrip(markdown: string): string {
  const editor = new Editor({ extensions: editorExtensions, content: markdownToJson(markdown) })
  try {
    return jsonToMarkdown(editor.getJSON())
  } finally {
    editor.destroy()
  }
}

/** A one-column table doc whose single body cell holds the given inline content. */
function tableDocWithCell(cellInline: JSONContent[]): JSONContent {
  return {
    type: 'doc',
    content: [
      {
        type: 'table',
        content: [
          {
            type: 'tableRow',
            content: [{ type: 'tableHeader', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'h' }] }] }],
          },
          { type: 'tableRow', content: [{ type: 'tableCell', content: [{ type: 'paragraph', content: cellInline }] }] },
        ],
      },
    ],
  }
}

describe('cell text the editor can type must serialize into parseable, byte-stable Markdown', () => {
  it('a typed pipe is escaped so it cannot split the cell', () => {
    const markdown = jsonToMarkdown(tableDocWithCell([{ type: 'text', text: 'a|b' }]))
    expect(markdown).toBe('| h |\n| --- |\n| a\\|b |\n')
    expect(roundTrip(markdown)).toBe(markdown)
    // and it parses back to the typed text, not to two cells
    const row = markdownToJson(markdown).content![0].content![1]
    expect(row.content).toHaveLength(1)
    expect(row.content![0].content![0].content![0].text).toBe('a|b')
  })

  it('a typed backslash-then-pipe survives (backslash doubled before the escaped pipe)', () => {
    const markdown = jsonToMarkdown(tableDocWithCell([{ type: 'text', text: 'a\\|b' }]))
    expect(markdown).toBe('| h |\n| --- |\n| a\\\\\\|b |\n')
    expect(roundTrip(markdown)).toBe(markdown)
    const row = markdownToJson(markdown).content![0].content![1]
    expect(row.content![0].content![0].content![0].text).toBe('a\\|b')
  })

  it('a typed pipe inside inline code is escaped too (code spans do not protect pipes from the cell split)', () => {
    const markdown = jsonToMarkdown(tableDocWithCell([{ type: 'text', text: 'a|b', marks: [{ type: 'code' }] }]))
    expect(markdown).toBe('| h |\n| --- |\n| `a\\|b` |\n')
    expect(roundTrip(markdown)).toBe(markdown)
    const cellText = markdownToJson(markdown).content![0].content![1].content![0].content![0].content![0]
    expect(cellText.text).toBe('a|b')
    expect(cellText.marks).toEqual([{ type: 'code' }])
  })

  it('a cell whose entire text is ^^ is escaped so it does not become a rowspan marker', () => {
    const markdown = jsonToMarkdown(tableDocWithCell([{ type: 'text', text: '^^' }]))
    expect(markdown).toBe('| h |\n| --- |\n| \\^^ |\n')
    expect(roundTrip(markdown)).toBe(markdown)
    // ^^ as PART of cell text needs no escape — multimd only merges exact-^^ cells
    const partial = jsonToMarkdown(tableDocWithCell([{ type: 'text', text: '^^ but longer' }]))
    expect(partial).toBe('| h |\n| --- |\n| ^^ but longer |\n')
    expect(roundTrip(partial)).toBe(partial)
  })

  it('multiple paragraphs in one cell (paste, block joins) degrade to <br> instead of losing content', () => {
    const doc = tableDocWithCell([])
    doc.content![0].content![1].content![0].content = [
      { type: 'paragraph', content: [{ type: 'text', text: 'first' }] },
      { type: 'paragraph', content: [{ type: 'text', text: 'second' }] },
    ]
    const markdown = jsonToMarkdown(doc)
    expect(markdown).toBe('| h |\n| --- |\n| first<br>second |\n')
    expect(roundTrip(markdown)).toBe(markdown)
  })

  it('a hardBreak at the very end of a cell serializes and survives', () => {
    const markdown = jsonToMarkdown(tableDocWithCell([{ type: 'text', text: 'a' }, { type: 'hardBreak' }]))
    expect(markdown).toBe('| h |\n| --- |\n| a<br> |\n')
    expect(roundTrip(markdown)).toBe(markdown)
  })
})

describe('mixed-alignment column rule', () => {
  it('the topmost cell anchored at a column decides its delimiter; divergent aligns deeper down normalize', () => {
    // Header cell says center, body cell in the same column says right —
    // Markdown has exactly one alignment per column, so the topmost anchor
    // (the header cell here) wins and the body cell's divergence is not
    // representable. The next load assigns every cell the column alignment.
    const doc: JSONContent = {
      type: 'doc',
      content: [
        {
          type: 'table',
          content: [
            {
              type: 'tableRow',
              content: [
                {
                  type: 'tableHeader',
                  attrs: { align: 'center' },
                  content: [{ type: 'paragraph', content: [{ type: 'text', text: 'h' }] }],
                },
              ],
            },
            {
              type: 'tableRow',
              content: [
                {
                  type: 'tableCell',
                  attrs: { align: 'right' },
                  content: [{ type: 'paragraph', content: [{ type: 'text', text: 'b' }] }],
                },
              ],
            },
          ],
        },
      ],
    }
    const markdown = jsonToMarkdown(doc)
    expect(markdown).toBe('| h |\n| :---: |\n| b |\n')
    // …and re-loading yields center on BOTH cells (the normalization), stably.
    expect(roundTrip(markdown)).toBe(markdown)
    const reloaded = markdownToJson(markdown)
    const cells = reloaded.content![0].content!.map((row) => row.content![0])
    expect(cells.map((c) => c.attrs?.align)).toEqual(['center', 'center'])
  })

  it('a column whose header slot is covered by a colspan takes its delimiter from the body cell anchored there', () => {
    const markdown = '| G || c |\n| :--- | :---: | ---: |\n| 1 | 2 | 3 |\n'
    expect(roundTrip(markdown)).toBe(markdown)
    // The spanning header cell itself carries its anchor column's alignment.
    const header = markdownToJson(markdown).content![0].content![0].content![0]
    expect(header.attrs).toMatchObject({ colspan: 2, align: 'left' })
  })
})

describe('unsupported table shapes stay loud (no silent data loss)', () => {
  it('table captions are rejected', () => {
    expect(() => markdownToJson('| a |\n| --- |\n| 1 |\n[caption]\n')).toThrow(/caption/)
  })

  it('multi-row headers are rejected', () => {
    expect(() => markdownToJson('| a |\n| b |\n| --- |\n| 1 |\n')).toThrow(/one header row/)
  })

  it('non-paragraph blocks inside a cell are rejected at serialize time', () => {
    const doc = tableDocWithCell([])
    doc.content![0].content![1].content![0].content = [
      { type: 'bulletList', content: [{ type: 'listItem', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'x' }] }] }] },
    ]
    expect(() => jsonToMarkdown(doc)).toThrow(/table cell/)
  })
})

describe('span structure is valid against the real ProseMirror table schema', () => {
  const complex = [
    '| Stage | Result | Notes |',
    '| :--- | :---: | ---: |',
    '| Boost | go<br>go | nominal |',
    '| Coast || **hold** |',
    '| ^^ || 3 |',
    '',
  ].join('\n')

  it('nodeFromJSON + check() passes and TableMap reports a consistent, problem-free grid', () => {
    const schema = getSchema(editorExtensions)
    const doc = schema.nodeFromJSON(markdownToJson(complex))
    doc.check() // throws on schema violations
    const table = doc.child(0)
    expect(table.type.spec.tableRole).toBe('table')
    const map = TableMap.get(table)
    expect(map.width).toBe(3)
    expect(map.height).toBe(4)
    // prosemirror-tables records structural inconsistencies (collisions,
    // missing cells, overlong rowspans) here; a clean merged table has none.
    expect(map.problems ?? null).toBeNull()
  })

  it('a row fully absorbed by rowspans is schema-valid and mapped', () => {
    const schema = getSchema(editorExtensions)
    const doc = schema.nodeFromJSON(markdownToJson('| a | b |\n| --- | --- |\n| t1 | t2 |\n| ^^ | ^^ |\n| 1 | 2 |\n'))
    doc.check()
    const map = TableMap.get(doc.child(0))
    expect(map.width).toBe(2)
    expect(map.height).toBe(4)
    expect(map.problems ?? null).toBeNull()
  })
})

describe('the importer fixture contract (phase 2 unblocks on this exact behaviour)', () => {
  it('a space-padded empty cell (`|  |`) is an empty cell, never a colspan merge', () => {
    // tests/fixtures/converted-markdown/table-merged-cells-degraded.md
    // depends on this: the degraded form must keep parsing as three cells
    // until the phase-2 importer starts emitting real spans.
    const row = markdownToJson('| A | B | C |\n| --- | --- | --- |\n| Merged AB |  | c1 |\n').content![0].content![1]
    expect(row.content).toHaveLength(3)
    expect(row.content![1].attrs?.colspan).toBeUndefined()
  })
})
