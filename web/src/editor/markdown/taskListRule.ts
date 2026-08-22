import type { StateCore, Token } from 'markdown-it'

/**
 * GFM task lists (`- [ ] …` / `- [x] …`) aren't a markdown-it core rule —
 * they're a bullet list where each item's text happens to start with a
 * checkbox marker. This core-chain rule runs after block parsing and
 * rewrites `bullet_list_open`/`list_item_open` tokens to
 * `task_list_open`/`task_list_item_open` wherever *every* item in the list
 * matches the marker, stripping the marker text so it isn't duplicated in
 * the rendered/parsed content.
 *
 * A list with a mix of task and non-task items is left as a plain bullet
 * list (GFM itself permits the mix; TipTap's TaskList node does not, since
 * it's a distinct node type from BulletList) — a known, narrow limitation,
 * not expected to matter for real content.
 */
const TASK_MARKER = /^\[([ xX])\]\s+/

interface PendingTaskItem {
  openIdx: number
  inlineIdx: number
  checked: boolean
}

export function taskListRule(state: StateCore): void {
  const tokens = state.tokens
  let i = 0

  while (i < tokens.length) {
    const openTok = tokens[i]

    if (openTok.type === 'bullet_list_open') {
      const listLevel = openTok.level
      const items: PendingTaskItem[] = []
      let allTasks = true
      let pendingOpenIdx = -1
      let closeIdx = -1

      let j = i + 1
      while (j < tokens.length) {
        const tok = tokens[j]

        if (tok.type === 'bullet_list_close' && tok.level === listLevel) {
          closeIdx = j
          break
        }

        if (tok.type === 'list_item_open' && tok.level === listLevel + 1) {
          pendingOpenIdx = j
        } else if (tok.type === 'list_item_close' && tok.level === listLevel + 1) {
          pendingOpenIdx = -1
        } else if (
          tok.type === 'inline' &&
          tok.level === listLevel + 3 &&
          pendingOpenIdx !== -1 &&
          !items.some((item) => item.openIdx === pendingOpenIdx)
        ) {
          const match = TASK_MARKER.exec(tok.content)
          if (match) {
            items.push({ openIdx: pendingOpenIdx, inlineIdx: j, checked: match[1].toLowerCase() === 'x' })
          } else {
            allTasks = false
          }
        }

        j++
      }

      if (closeIdx !== -1 && items.length > 0 && allTasks) {
        openTok.type = 'task_list_open'
        tokens[closeIdx].type = 'task_list_close'

        for (const item of items) {
          tokens[item.openIdx].type = 'task_list_item_open'
          tokens[item.openIdx].meta = { checked: item.checked }

          let k = item.openIdx + 1
          while (!(tokens[k].type === 'list_item_close' && tokens[k].level === listLevel + 1)) {
            k++
          }
          tokens[k].type = 'task_list_item_close'

          const inlineTok: Token = tokens[item.inlineIdx]
          const match = TASK_MARKER.exec(inlineTok.content)
          if (match) {
            inlineTok.content = inlineTok.content.slice(match[0].length)
            const firstChild = inlineTok.children?.[0]
            if (firstChild && firstChild.type === 'text') {
              firstChild.content = firstChild.content.slice(match[0].length)
            }
          }
        }
      }
    }

    i++
  }
}
