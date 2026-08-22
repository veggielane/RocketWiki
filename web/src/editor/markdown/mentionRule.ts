import type { StateInline } from 'markdown-it'

/**
 * Matches `@[display](user://{id})` (design.md §4) as a single atomic
 * token, rather than letting the default parser split it into a literal
 * "@" text run followed by a normal link — mentions are not editable text
 * with a link mark, they're an atomic reference (see nodes/Mention.ts).
 */
const MENTION_RE = /^@\[([^\]]*)\]\(user:\/\/([^)\s]+)\)/

export function mentionRule(state: StateInline, silent: boolean): boolean {
  if (state.src.charCodeAt(state.pos) !== 0x40 /* @ */) {
    return false
  }

  const match = MENTION_RE.exec(state.src.slice(state.pos))
  if (!match) {
    return false
  }

  if (!silent) {
    const token = state.push('mention', '', 0)
    token.meta = { display: match[1], userId: match[2] }
    token.content = match[0]
  }

  state.pos += match[0].length
  return true
}
