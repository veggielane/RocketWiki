import type { JSONContent } from '@tiptap/core'

type MarkJSON = NonNullable<JSONContent['marks']>[number]

/** TipTap `JSONContent` doc -> Markdown source text. The other half of the round trip. */
export function jsonToMarkdown(doc: JSONContent): string {
  const blocks = (doc.content ?? []).map(serializeBlock)
  return blocks.length > 0 ? `${blocks.join('\n\n')}\n` : ''
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
      // with the reserved `drawio` language, payload emitted verbatim.
      const payload = (node.attrs?.payload as string | undefined) ?? ''
      return `\`\`\`drawio\n${payload}\n\`\`\``
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

function serializeTable(node: JSONContent): string {
  const rows = node.content ?? []
  const [headerRow, ...bodyRows] = rows
  const headerCells = (headerRow?.content ?? []).map(serializeCell)
  const headerLine = `| ${headerCells.join(' | ')} |`
  const separatorLine = `| ${headerCells.map(() => '---').join(' | ')} |`
  const bodyLines = bodyRows.map((row) => `| ${(row.content ?? []).map(serializeCell).join(' | ')} |`)
  return [headerLine, separatorLine, ...bodyLines].join('\n')
}

function serializeCell(cell: JSONContent): string {
  const paragraph = cell.content?.[0]
  return paragraph ? serializeInline(paragraph.content ?? []) : ''
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
  if (mark.type === 'link' || mark.type === 'pageLink') {
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
  const pair = MARK_DELIMITERS[mark.type]
  if (!pair) {
    throw new Error(`jsonToMarkdown: unsupported mark "${mark.type}"`)
  }
  return pair[1]
}

function marksEqual(a: MarkJSON, b: MarkJSON): boolean {
  if (a.type !== b.type) return false
  if (a.type === 'link' || a.type === 'pageLink') {
    return JSON.stringify(a.attrs ?? null) === JSON.stringify(b.attrs ?? null)
  }
  return true
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
function serializeInline(nodes: JSONContent[]): string {
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
      emit(node.marks ?? [], node.text ?? '')
    } else if (node.type === 'hardBreak') {
      emit(node.marks ?? [], '')
      out += '  \n'
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
