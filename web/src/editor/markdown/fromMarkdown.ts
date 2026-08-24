import type { JSONContent } from '@tiptap/core'
import type { Token } from 'markdown-it'
import { createMarkdownIt } from './markdownIt'
import type { CalloutType } from '../nodes/Callout'
import { TokenCursor } from './tokenCursor'
import { parseGitLabIssueTarget } from '../../gitlab/issueScheme'

type MarkJSON = NonNullable<JSONContent['marks']>[number]

const md = createMarkdownIt()

/** Markdown source text -> TipTap `JSONContent` doc, ready for `editor.commands.setContent`. */
export function markdownToJson(markdown: string): JSONContent {
  const tokens = md.parse(markdown, {})
  const cursor = new TokenCursor(tokens)
  const content = parseBlocks(cursor)
  return { type: 'doc', content }
}

function withContent(content: JSONContent[]): Pick<JSONContent, 'content'> {
  return content.length > 0 ? { content } : {}
}

function parseBlocks(cursor: TokenCursor): JSONContent[] {
  const nodes: JSONContent[] = []
  while (cursor.hasNext() && cursor.peek().nesting !== -1) {
    nodes.push(parseBlock(cursor))
  }
  return nodes
}

function parseBlock(cursor: TokenCursor): JSONContent {
  const tok = cursor.peek()

  if (tok.type === 'paragraph_open') {
    cursor.next()
    const inlineTok = cursor.next()
    cursor.next() // paragraph_close
    return { type: 'paragraph', ...withContent(parseInlineChildren(inlineTok.children ?? [])) }
  }

  if (tok.type === 'heading_open') {
    cursor.next()
    const level = Number(tok.tag.slice(1))
    const inlineTok = cursor.next()
    cursor.next() // heading_close
    return { type: 'heading', attrs: { level }, ...withContent(parseInlineChildren(inlineTok.children ?? [])) }
  }

  if (tok.type === 'blockquote_open') {
    cursor.next()
    const content = parseBlocks(cursor)
    cursor.next() // blockquote_close
    return { type: 'blockquote', ...withContent(content) }
  }

  if (tok.type === 'bullet_list_open') {
    return parseList(cursor, 'bulletList')
  }

  if (tok.type === 'ordered_list_open') {
    return parseList(cursor, 'orderedList')
  }

  if (tok.type === 'task_list_open') {
    cursor.next()
    const items: JSONContent[] = []
    while (cursor.peek().type === 'task_list_item_open') {
      const checked = Boolean(cursor.peek().meta?.checked)
      items.push(parseListItem(cursor, 'taskItem', { checked }))
    }
    cursor.next() // task_list_close
    return { type: 'taskList', content: items }
  }

  if (tok.type === 'fence') {
    cursor.next()
    const language = tok.info.trim()
    const code = tok.content.replace(/\n$/, '')
    if (language === 'drawio') {
      // ```drawio is the reserved storage form of the draw.io diagram node
      // (base64 editable-SVG payload — see nodes/DrawioDiagram.ts). An
      // optional first line `alt: <text>` carries the author's alt text —
      // in the fence *body* (like mermaid's accTitle/accDescr) so the fence
      // stays inert text to everything outside the SPA. The line cannot
      // collide with a real payload (`:` and space are not base64), and the
      // match requires the trailing newline so an alt-line-only body is not
      // reshaped; everything after it is the payload verbatim, so whatever
      // bytes were stored serialize back out unchanged even when they're
      // not valid base64.
      const altMatch = /^alt: (.+)\n/.exec(code)
      return {
        type: 'drawioDiagram',
        attrs: {
          payload: altMatch ? code.slice(altMatch[0].length) : code,
          alt: altMatch ? altMatch[1] : '',
        },
      }
    }
    return {
      type: 'codeBlock',
      attrs: { language: language.length > 0 ? language : null },
      ...(code.length > 0 ? { content: [{ type: 'text', text: code }] } : {}),
    }
  }

  if (tok.type === 'table_open') {
    return parseTable(cursor)
  }

  if (tok.type === 'hr') {
    cursor.next()
    return { type: 'horizontalRule' }
  }

  const containerMatch = /^container_(.+)_open$/.exec(tok.type)
  if (containerMatch) {
    cursor.next()
    const calloutType = containerMatch[1] as CalloutType
    const content = parseBlocks(cursor)
    cursor.next() // container_*_close
    return { type: 'callout', attrs: { calloutType }, ...withContent(content) }
  }

  throw new Error(`markdownToJson: unsupported block token "${tok.type}"`)
}

function parseList(cursor: TokenCursor, type: 'bulletList' | 'orderedList'): JSONContent {
  const open = cursor.next()
  const items: JSONContent[] = []
  while (cursor.peek().type === 'list_item_open') {
    items.push(parseListItem(cursor, 'listItem'))
  }
  cursor.next() // *_list_close

  if (type === 'orderedList') {
    const startAttr = open.attrGet('start')
    return { type, attrs: { start: startAttr ? Number(startAttr) : 1 }, content: items }
  }
  return { type, content: items }
}

function parseListItem(
  cursor: TokenCursor,
  type: 'listItem' | 'taskItem',
  extraAttrs?: Record<string, unknown>,
): JSONContent {
  cursor.next() // list_item_open / task_list_item_open
  const content = parseBlocks(cursor)
  cursor.next() // list_item_close / task_list_item_close
  return { type, ...(extraAttrs ? { attrs: extraAttrs } : {}), ...withContent(content) }
}

type ColumnAlign = 'left' | 'center' | 'right'

/**
 * markdown-it-multimd-table records the delimiter row verbatim on the
 * `table_open` token: `meta.sep.aligns` is one entry per COLUMN —
 * `""` (no colons), `"left"`, `"center"`, or `"right"`. This is the only
 * complete source of per-column alignment: the `style` attr on individual
 * th/td tokens carries just the cell's own anchor column, so a header cell
 * spanning mixed-aligned columns would lose the columns it covers.
 */
function columnAlignsFromMeta(tableOpen: Token): (ColumnAlign | null)[] {
  const meta = tableOpen.meta as { sep?: { aligns?: unknown[] } } | null
  const aligns = meta?.sep?.aligns ?? []
  return aligns.map((a) => (a === 'left' || a === 'center' || a === 'right' ? a : null))
}

function parseTable(cursor: TokenCursor): JSONContent {
  const open = cursor.next() // table_open
  const aligns = columnAlignsFromMeta(open)
  const rows: JSONContent[] = []
  // How many further rows each column stays covered by a rowspan opened in
  // an earlier row — a covered column produces NO cell token in later rows,
  // so column positions can only be tracked by replaying the spans.
  const pendingRowspans: number[] = []

  if (cursor.peek().type === 'caption_open') {
    // MultiMarkdown table captions (`[caption]` on the line directly above
    // or below a table) have no editor representation and the serializer
    // never emits them — silently dropping one would violate the §4
    // round-trip rule, so refuse loudly like any other unsupported token.
    throw new Error('markdownToJson: table captions ("[…]" adjacent to a table) are not supported')
  }

  if (cursor.peek().type === 'thead_open') {
    cursor.next()
    let headerRows = 0
    while (cursor.peek().type === 'tr_open') {
      if (headerRows === 1) {
        // multimd accepts several source lines above the delimiter row as a
        // multi-row header. The editor's table model has exactly one header
        // row (row 0), so a second one could only be silently reshaped into
        // a body row — a §4 violation. Refuse loudly instead.
        throw new Error('markdownToJson: tables with more than one header row are not supported')
      }
      rows.push(parseTableRow(cursor, 'tableHeader', aligns, pendingRowspans))
      headerRows += 1
    }
    cursor.next() // thead_close
  }

  if (cursor.peek().type === 'tbody_open') {
    cursor.next()
    while (cursor.peek().type === 'tr_open') {
      rows.push(parseTableRow(cursor, 'tableCell', aligns, pendingRowspans))
    }
    cursor.next() // tbody_close
  }

  cursor.next() // table_close
  return { type: 'table', content: rows }
}

function parseTableRow(
  cursor: TokenCursor,
  cellType: 'tableHeader' | 'tableCell',
  aligns: (ColumnAlign | null)[],
  pendingRowspans: number[],
): JSONContent {
  cursor.next() // tr_open
  const cells: JSONContent[] = []
  let col = 0
  const skipCoveredColumns = () => {
    while ((pendingRowspans[col] ?? 0) > 0) {
      pendingRowspans[col] -= 1
      col += 1
    }
  }

  skipCoveredColumns()
  while (cursor.peek().type === 'th_open' || cursor.peek().type === 'td_open') {
    const openTok = cursor.next()
    const inlineTok = cursor.peek().type === 'inline' ? cursor.next() : null
    cursor.next() // th_close / td_close

    const colspan = Number(openTok.attrGet('colspan') ?? 1)
    const rowspan = Number(openTok.attrGet('rowspan') ?? 1)
    const align = aligns[col] ?? null

    const attrs: Record<string, unknown> = {}
    if (colspan > 1) attrs.colspan = colspan
    if (rowspan > 1) attrs.rowspan = rowspan
    if (align !== null) attrs.align = align

    const inlineContent = inlineTok ? parseInlineChildren(inlineTok.children ?? [], { inTableCell: true }) : []
    cells.push({
      type: cellType,
      ...(Object.keys(attrs).length > 0 ? { attrs } : {}),
      content: [{ type: 'paragraph', ...withContent(inlineContent) }],
    })

    if (rowspan > 1) {
      for (let c = col; c < col + colspan; c++) {
        pendingRowspans[c] = (pendingRowspans[c] ?? 0) + (rowspan - 1)
      }
    }
    col += colspan
    skipCoveredColumns()
  }
  cursor.next() // tr_close
  return { type: 'tableRow', content: cells }
}

/**
 * The `<br>` forms recognised inside table cells (design.md §4: cell
 * newlines). `<br>` is canonical; `<br/>`, `<br />`, and case variants are
 * accepted on the way in and normalize to `<br>` on the next save — the
 * same one-way-canonicalization posture as `_x_` → `*x*`.
 */
const CELL_BR_PATTERN = /<br\s*\/?>/gi

interface InlineParseOptions {
  /**
   * Inside a table cell: literal `<br>` text becomes a hardBreak node
   * (GFM's only in-cell newline representation — a real newline would end
   * the row), and `\|` inside inline code sheds its backslash (the block
   * parser needs the escape to not split the cell, but unlike plain text —
   * where markdown-it unescapes automatically — code spans keep the raw
   * backslash; toMarkdown.ts re-escapes symmetrically).
   */
  inTableCell?: boolean
}

function parseInlineChildren(children: Token[], opts: InlineParseOptions = {}): JSONContent[] {
  const result: JSONContent[] = []
  const stack: MarkJSON[] = []

  const activeMarks = (): MarkJSON[] | undefined => (stack.length > 0 ? stack.map((m) => ({ ...m })) : undefined)

  for (const child of children) {
    switch (child.type) {
      case 'text': {
        if (opts.inTableCell) {
          // Split on <br> variants; the segments stay text, the separators
          // become hardBreak nodes carrying the current mark stack (so a
          // break inside `**bold<br>text**` doesn't sever the bold span).
          const segments = child.content.split(CELL_BR_PATTERN)
          segments.forEach((segment, i) => {
            if (segment.length > 0) {
              result.push({ type: 'text', text: segment, ...(activeMarks() ? { marks: activeMarks() } : {}) })
            }
            if (i < segments.length - 1) {
              result.push({ type: 'hardBreak', ...(activeMarks() ? { marks: activeMarks() } : {}) })
            }
          })
          break
        }
        if (child.content.length > 0) {
          result.push({ type: 'text', text: child.content, ...(activeMarks() ? { marks: activeMarks() } : {}) })
        }
        break
      }
      case 'softbreak': {
        // A literal newline inside one paragraph's source has no distinct
        // ProseMirror-node representation (see markdown/toMarkdown.ts) — it
        // round-trips as a plain space, matching how the wrapped text would
        // re-flow anyway. A true hard break needs GFM's trailing-two-spaces
        // or backslash form, which markdown-it reports as `hardbreak`.
        result.push({ type: 'text', text: ' ', ...(activeMarks() ? { marks: activeMarks() } : {}) })
        break
      }
      case 'hardbreak':
        // Carries the current mark stack so `**a  \nb**` keeps its bold
        // span open across the break instead of serializing as
        // `**a**  \n**b**`.
        result.push({ type: 'hardBreak', ...(activeMarks() ? { marks: activeMarks() } : {}) })
        break
      case 'code_inline': {
        const marks = [...stack, { type: 'code' }]
        // In a table cell, `\|` was required to stop the pipe from ending
        // the cell; markdown-it leaves the backslash in code-span content
        // (unlike plain text), so shed it here. Exact inverse of
        // toMarkdown.ts's escapeCellPipes: `\\` directly before an escaped
        // pipe collapses back to `\`, then `\|` back to `|`.
        const text = opts.inTableCell
          ? child.content.replace(/\\\\(?=\\\|)|\\\|/g, (m) => (m === '\\|' ? '|' : '\\'))
          : child.content
        result.push({ type: 'text', text, marks })
        break
      }
      case 'strong_open':
        stack.push({ type: 'bold' })
        break
      case 'em_open':
        stack.push({ type: 'italic' })
        break
      case 's_open':
        stack.push({ type: 'strike' })
        break
      case 'strong_close':
      case 'em_close':
      case 's_close':
      case 'link_close':
        // Well-formed by construction: markdown-it never emits overlapping
        // emphasis/strike/link spans, so the innermost open mark is always
        // the one being closed.
        stack.pop()
        break
      case 'link_open': {
        const href = String(child.attrGet('href') ?? '')
        const title = child.attrGet('title')
        // `[text](gitlab-issue://{project}/{iid})` becomes its own mark
        // (design.md §18) — but only the exact well-formed, title-less
        // shape. Anything else (non-numeric iid, empty project, a title)
        // stays an ordinary link so the author's bytes survive the round
        // trip instead of being "fixed" into the scheme.
        const gitlabIssue = title === null ? parseGitLabIssueTarget(href) : null
        if (gitlabIssue) {
          stack.push({ type: 'gitlabIssueLink', attrs: { project: gitlabIssue.project, iid: gitlabIssue.iid } })
        } else if (href.startsWith('page://')) {
          stack.push({ type: 'pageLink', attrs: { pageId: href.slice('page://'.length) } })
        } else {
          stack.push({ type: 'link', attrs: { href, title: title !== null ? String(title) : null } })
        }
        break
      }
      case 'image': {
        const title = child.attrGet('title')
        result.push({
          type: 'image',
          attrs: {
            src: String(child.attrGet('src') ?? ''),
            alt: child.content.length > 0 ? child.content : null,
            title: title !== null ? String(title) : null,
          },
        })
        break
      }
      case 'mention':
        result.push({
          type: 'mention',
          attrs: {
            userId: (child.meta as { userId: string; display: string }).userId,
            display: (child.meta as { userId: string; display: string }).display,
          },
        })
        break
      default:
        throw new Error(`markdownToJson: unsupported inline token "${child.type}"`)
    }
  }

  return result
}
