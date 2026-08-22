import type { JSONContent } from '@tiptap/core'
import type { Token } from 'markdown-it'
import { createMarkdownIt } from './markdownIt'
import type { CalloutType } from '../nodes/Callout'
import { TokenCursor } from './tokenCursor'

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

function parseTable(cursor: TokenCursor): JSONContent {
  cursor.next() // table_open
  const rows: JSONContent[] = []

  if (cursor.peek().type === 'thead_open') {
    cursor.next()
    while (cursor.peek().type === 'tr_open') {
      rows.push(parseTableRow(cursor, 'tableHeader'))
    }
    cursor.next() // thead_close
  }

  if (cursor.peek().type === 'tbody_open') {
    cursor.next()
    while (cursor.peek().type === 'tr_open') {
      rows.push(parseTableRow(cursor, 'tableCell'))
    }
    cursor.next() // tbody_close
  }

  cursor.next() // table_close
  return { type: 'table', content: rows }
}

function parseTableRow(cursor: TokenCursor, cellType: 'tableHeader' | 'tableCell'): JSONContent {
  cursor.next() // tr_open
  const cells: JSONContent[] = []
  while (cursor.peek().type === 'th_open' || cursor.peek().type === 'td_open') {
    cursor.next()
    const inlineTok = cursor.peek().type === 'inline' ? cursor.next() : null
    cursor.next() // th_close / td_close
    const inlineContent = inlineTok ? parseInlineChildren(inlineTok.children ?? []) : []
    cells.push({ type: cellType, content: [{ type: 'paragraph', ...withContent(inlineContent) }] })
  }
  cursor.next() // tr_close
  return { type: 'tableRow', content: cells }
}

function parseInlineChildren(children: Token[]): JSONContent[] {
  const result: JSONContent[] = []
  const stack: MarkJSON[] = []

  const activeMarks = (): MarkJSON[] | undefined => (stack.length > 0 ? stack.map((m) => ({ ...m })) : undefined)

  for (const child of children) {
    switch (child.type) {
      case 'text': {
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
        result.push({ type: 'hardBreak' })
        break
      case 'code_inline': {
        const marks = [...stack, { type: 'code' }]
        result.push({ type: 'text', text: child.content, marks })
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
        if (href.startsWith('page://')) {
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
