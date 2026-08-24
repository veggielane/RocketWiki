import type { JSONContent } from '@tiptap/core'

type MarkJSON = NonNullable<JSONContent['marks']>[number]

/**
 * TipTap `JSONContent` doc -> Markdown source text. The other half of the
 * round trip.
 *
 * Top-level EMPTY paragraphs are dropped before serialization. They are
 * editor chrome, not content: StarterKit v3's TrailingNode keeps an empty
 * paragraph after a trailing code block / table so the editor stays
 * clickable below it, and GFM has no representation for an empty
 * paragraph at all — serializing one emits only blank lines, which
 * `markdownToJson` then (correctly) parses as nothing, so keeping them
 * would break §4's byte-identity on the very next load. Markdown produced
 * by the parser never contains them, so dropping them here is exactly the
 * fixed point the round-trip rule demands.
 */
export function jsonToMarkdown(doc: JSONContent): string {
  const blocks = (doc.content ?? []).filter((node) => !isEmptyParagraph(node)).map(serializeBlock)
  return blocks.length > 0 ? `${blocks.join('\n\n')}\n` : ''
}

function isEmptyParagraph(node: JSONContent): boolean {
  return node.type === 'paragraph' && (node.content === undefined || node.content.length === 0)
}

function serializeChildBlocks(content: JSONContent[]): string {
  return content.map(serializeBlock).join('\n\n')
}

function serializeBlock(node: JSONContent): string {
  switch (node.type) {
    case 'paragraph':
      return serializeInline(node.content ?? [])

    case 'heading': {
      const level = (node.attrs?.level as number | undefined) ?? 1
      return `${'#'.repeat(level)} ${serializeInline(node.content ?? [])}`
    }

    case 'blockquote': {
      const inner = serializeChildBlocks(node.content ?? [])
      return inner
        .split('\n')
        .map((line) => (line.length > 0 ? `> ${line}` : '>'))
        .join('\n')
    }

    case 'bulletList':
      return serializeList(node.content ?? [], () => ({ marker: '-', indentWidth: 2 }))

    case 'orderedList': {
      const start = (node.attrs?.start as number | undefined) ?? 1
      return serializeList(node.content ?? [], (i) => {
        const marker = `${start + i}.`
        return { marker, indentWidth: marker.length + 1 }
      })
    }

    case 'taskList':
      // The checkbox (`[ ]`/`[x]`) is GFM task-list *content* layered on
      // top of a plain bullet, not part of the structural list marker
      // (CommonMark's own marker grammar for a `-` bullet is just "- ").
      // Continuation/nested-list indent therefore stays 2, matching a
      // plain bullet, even though the first line reads "- [ ] text".
      return serializeList(node.content ?? [], (_i, item) => ({
        marker: `- [${item?.attrs?.checked ? 'x' : ' '}]`,
        indentWidth: 2,
      }))

    case 'codeBlock': {
      const language = (node.attrs?.language as string | null) ?? ''
      const code = (node.content ?? []).map((t) => t.text ?? '').join('')
      return `\`\`\`${language}\n${code}\n\`\`\``
    }

    case 'table':
      return serializeTable(node)

    case 'drawioDiagram': {
      // The mirror of fromMarkdown.ts's drawio branch: a plain fenced block
      // with the reserved `drawio` language, an optional `alt: <text>`
      // first line (author alt text — omitted entirely when empty, so
      // pre-alt pages stay byte-identical), payload emitted verbatim.
      const payload = (node.attrs?.payload as string | undefined) ?? ''
      const alt = (node.attrs?.alt as string | undefined) ?? ''
      const altLine = alt.length > 0 ? `alt: ${alt}\n` : ''
      return `\`\`\`drawio\n${altLine}${payload}\n\`\`\``
    }

    case 'callout': {
      const calloutType = (node.attrs?.calloutType as string | undefined) ?? 'info'
      const inner = serializeChildBlocks(node.content ?? [])
      return `:::${calloutType}\n${inner}\n:::`
    }

    case 'horizontalRule':
      return '---'

    default:
      throw new Error(`jsonToMarkdown: unsupported block node "${node.type}"`)
  }
}

interface ListMarker {
  marker: string
  indentWidth: number
}

function serializeList(items: JSONContent[], markerFor: (index: number, item: JSONContent) => ListMarker): string {
  return items.map((item, i) => serializeListItemLike(item, markerFor(i, item))).join('\n')
}

function serializeListItemLike(item: JSONContent, { marker, indentWidth }: ListMarker): string {
  const children = item.content ?? []
  const [first, ...rest] = children
  const firstLine = `${marker} ${first ? serializeBlock(first) : ''}`
  const indent = ' '.repeat(indentWidth)

  const restLines = rest.map((child) =>
    serializeBlock(child)
      .split('\n')
      .map((line) => (line.length > 0 ? indent + line : line))
      .join('\n'),
  )

  return [firstLine, ...restLines].join('\n')
}

type ColumnAlign = 'left' | 'center' | 'right'

interface GridSlot {
  cell: JSONContent
  anchorRow: number
  anchorCol: number
}

/**
 * Expands a table's rows (which, per the ProseMirror table model, contain
 * only the cells that START in each row) into a full occupancy grid, so
 * every column position knows which cell covers it. This is what lets the
 * serializer emit multimd-table's span syntax: a colspan continuation is an
 * immediately-adjacent `|`, a rowspan continuation is a `^^` cell.
 */
function buildTableGrid(rows: JSONContent[]): { width: number; slots: (GridSlot | undefined)[][] } {
  const slots: (GridSlot | undefined)[][] = rows.map(() => [])
  let width = 0
  rows.forEach((row, r) => {
    let col = 0
    for (const cell of row.content ?? []) {
      while (slots[r][col] !== undefined) col += 1
      const colspan = Number(cell.attrs?.colspan ?? 1)
      const rowspan = Number(cell.attrs?.rowspan ?? 1)
      for (let rr = r; rr < Math.min(r + rowspan, rows.length); rr++) {
        for (let cc = col; cc < col + colspan; cc++) {
          slots[rr][cc] = { cell, anchorRow: r, anchorCol: col }
        }
      }
      col += colspan
    }
    width = Math.max(width, slots[r].length)
  })
  return { width, slots }
}

function delimiterFor(align: ColumnAlign | null): string {
  switch (align) {
    case 'left':
      return ':---'
    case 'center':
      return ':---:'
    case 'right':
      return '---:'
    default:
      return '---'
  }
}

function cellAlign(cell: JSONContent): ColumnAlign | null {
  const align = cell.attrs?.align
  return align === 'left' || align === 'center' || align === 'right' ? align : null
}

function serializeTable(node: JSONContent): string {
  const rows = node.content ?? []
  const { width, slots } = buildTableGrid(rows)

  const serializeRowLine = (r: number): string => {
    let line = ''
    for (let c = 0; c < width; c++) {
      const slot = slots[r][c]
      if (slot === undefined) {
        line += '|  ' // structural hole — unreachable from parse or editor ops, kept as an explicit empty cell
      } else if (slot.anchorRow === r && slot.anchorCol === c) {
        line += `| ${serializeCell(slot.cell)} `
      } else if (slot.anchorRow < r && slot.anchorCol === c) {
        line += '| ^^ ' // rowspan continuation (leftmost column of the covering cell)
      } else {
        line += '|' // colspan continuation: an immediately-adjacent pipe
      }
    }
    return `${line}|`
  }

  // Delimiter-row alignment per column: the topmost cell ANCHORED at that
  // exact column wins (documented mixed-alignment rule) — this is also what
  // makes alignment byte-stable under header colspans, because the parser
  // assigned each cell the alignment of its own anchor column. A column no
  // cell anchors at (it is covered by spans in every row) falls back to the
  // topmost covering cell's alignment; the source delimiter for such a
  // column is unrecoverable and normalizes.
  const delimiterLine = () => {
    const parts: string[] = []
    for (let c = 0; c < width; c++) {
      let align: ColumnAlign | null = null
      let fallback: ColumnAlign | null = null
      let anchored = false
      for (let r = 0; r < rows.length; r++) {
        const slot = slots[r][c]
        if (slot === undefined) continue
        if (fallback === null) fallback = cellAlign(slot.cell)
        if (slot.anchorRow === r && slot.anchorCol === c) {
          align = cellAlign(slot.cell)
          anchored = true
          break
        }
      }
      parts.push(delimiterFor(anchored ? align : fallback))
    }
    return `| ${parts.join(' | ')} |`
  }

  const lines = [serializeRowLine(0), delimiterLine()]
  for (let r = 1; r < rows.length; r++) lines.push(serializeRowLine(r))
  return lines.join('\n')
}

function serializeCell(cell: JSONContent): string {
  // A cell's paragraphs join with `<br>` — the §4 in-cell newline. The
  // parser only ever produces a single paragraph per cell, so this is
  // byte-stable; multiple paragraphs (paste, block joins) degrade to
  // explicit breaks instead of silently dropping content.
  const text = (cell.content ?? [])
    .map((block) => {
      if (block.type !== 'paragraph') {
        throw new Error(`jsonToMarkdown: unsupported block "${block.type}" in a table cell — cells hold inline text only`)
      }
      return serializeInline(block.content ?? [], { inTableCell: true })
    })
    .join('<br>')
  // A cell whose entire content is `^^` would re-parse as a rowspan
  // continuation marker and merge into the cell above; the backslash keeps
  // it literal text (and `\^^` re-parses to exactly `^^`, so it is stable).
  return text.trim() === '^^' ? '\\^^' : text
}

// Canonical delimiters per design.md §4's "Canonical normalization" table —
// asterisk-based for both bold and italic, chosen for consistency over the
// arbitrary alternative of mixing `*`/`_`. markdown-it doesn't record which
// delimiter character the source used, so anything written with the other
// valid form (`_x_`, `__x__`) normalizes to these on save.
const MARK_DELIMITERS: Record<string, [string, string]> = {
  bold: ['**', '**'],
  italic: ['*', '*'],
  strike: ['~~', '~~'],
  code: ['`', '`'],
}

function openDelim(mark: MarkJSON): string {
  if (mark.type === 'link' || mark.type === 'pageLink' || mark.type === 'gitlabIssueLink') {
    return '['
  }
  const pair = MARK_DELIMITERS[mark.type]
  if (!pair) {
    throw new Error(`jsonToMarkdown: unsupported mark "${mark.type}"`)
  }
  return pair[0]
}

function closeDelim(mark: MarkJSON): string {
  if (mark.type === 'link') {
    const href = mark.attrs?.href as string
    const title = mark.attrs?.title ? ` "${mark.attrs.title as string}"` : ''
    return `](${href}${title})`
  }
  if (mark.type === 'pageLink') {
    return `](page://${mark.attrs?.pageId as string})`
  }
  if (mark.type === 'gitlabIssueLink') {
    // design.md §18 — scheme-only, host-free; attrs hold the author's raw
    // digits/path so this is byte-identical to what was parsed.
    return `](gitlab-issue://${mark.attrs?.project as string}/${mark.attrs?.iid as string})`
  }
  const pair = MARK_DELIMITERS[mark.type]
  if (!pair) {
    throw new Error(`jsonToMarkdown: unsupported mark "${mark.type}"`)
  }
  return pair[1]
}

function marksEqual(a: MarkJSON, b: MarkJSON): boolean {
  if (a.type !== b.type) return false
  if (a.type === 'link' || a.type === 'pageLink' || a.type === 'gitlabIssueLink') {
    return JSON.stringify(a.attrs ?? null) === JSON.stringify(b.attrs ?? null)
  }
  return true
}

interface InlineSerializeOptions {
  /**
   * Inside a table cell: a hardBreak becomes literal `<br>` (a real newline
   * would end the table row — see design.md §4), and `|` in text is escaped
   * as `\|` so it can't split the cell. The escape also covers code-marked
   * text — code spans do NOT protect pipes from the table's block-level
   * cell split — with any backslash directly before a pipe doubled first so
   * the escape stays unambiguous (fromMarkdown.ts undoes the code-span
   * escape symmetrically; plain text is unescaped by markdown-it itself).
   */
  inTableCell?: boolean
}

function escapeCellPipes(text: string): string {
  return text.replace(/\\(?=\|)|\|/g, (m) => (m === '|' ? '\\|' : '\\\\'))
}

/**
 * Walks a run of inline leaves (text/image/mention/hardBreak), each
 * carrying an ordered stack of marks, and emits markdown by diffing each
 * leaf's mark stack against the currently "open" delimiters — closing
 * whatever isn't shared with the previous leaf, then opening whatever's
 * new. This is what correctly handles partially-overlapping spans, e.g.
 * "**a *b* c**" (bold covering all three, italic covering only "b"),
 * without over- or under-closing delimiters.
 */
function serializeInline(nodes: JSONContent[], opts: InlineSerializeOptions = {}): string {
  let out = ''
  const openStack: MarkJSON[] = []

  const closeTo = (n: number) => {
    while (openStack.length > n) {
      out += closeDelim(openStack.pop()!)
    }
  }

  const emit = (marks: MarkJSON[], body: string) => {
    let common = 0
    while (common < openStack.length && common < marks.length && marksEqual(openStack[common], marks[common])) {
      common++
    }
    closeTo(common)
    for (let i = common; i < marks.length; i++) {
      out += openDelim(marks[i])
      openStack.push(marks[i])
    }
    out += body
  }

  for (const node of nodes) {
    if (node.type === 'text') {
      const text = node.text ?? ''
      emit(node.marks ?? [], opts.inTableCell ? escapeCellPipes(text) : text)
    } else if (node.type === 'hardBreak') {
      emit(node.marks ?? [], '')
      out += opts.inTableCell ? '<br>' : '  \n'
    } else if (node.type === 'image') {
      const alt = (node.attrs?.alt as string | null) ?? ''
      const title = node.attrs?.title ? ` "${node.attrs.title as string}"` : ''
      emit([], `![${alt}](${node.attrs?.src as string}${title})`)
    } else if (node.type === 'mention') {
      emit([], `@[${node.attrs?.display as string}](user://${node.attrs?.userId as string})`)
    } else {
      throw new Error(`jsonToMarkdown: unsupported inline node "${node.type}"`)
    }
  }

  closeTo(0)
  return out
}
