import type { GraphNode } from './graphModel'

/**
 * force-graph puts a node's label into its hover tooltip with `innerHTML`,
 * and a page title is whatever its author typed. Escaped here, once, so the
 * tooltip can never run a title as markup — the same reason the editor never
 * renders raw HTML (design.md §4).
 */
export function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
}

/**
 * The hover tooltip for a node: title, space, and the page's WHOLE marking
 * label — `label`, not `levelName`, because a tooltip is presenting "this
 * page's marking" and has the room (markings.graphql's rule).
 */
export function nodeTooltip(node: GraphNode): string {
  return `${escapeHtml(node.title)}<br/><small>${escapeHtml(node.spaceKey)} · ${escapeHtml(node.marking.label)}</small>`
}
