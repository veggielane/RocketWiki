export interface FlatComment {
  id: string
  parentCommentId: string | null
  body: string
  isDeleted: boolean
  authorUserId: string
  authorDisplayName: string
  createdAtUtc: string
  editedAtUtc: string | null
}

export interface CommentNode extends FlatComment {
  children: CommentNode[]
}

/**
 * design.md §5/data-model.md: comments are page-level and threaded, stored
 * flat with a `parentCommentId`. The server sends the flat list (simpler to
 * keep consistent after one add/delete than re-nesting server-side on every
 * mutation); this builds the reply tree client-side, sorted oldest-first at
 * every level so replies read top-to-bottom in the order they were posted.
 *
 * A deleted comment (`isDeleted: true`, body blanked) is **kept as a node**,
 * not dropped — that's the whole point of the tombstone (data-model.md:
 * "keeps thread shape, body blanked"): its replies would otherwise lose
 * their parent and float free, or have to be re-parented to something they
 * weren't actually replying to.
 *
 * Assumes well-formed input (no cycles) — the server is the source of
 * truth for comment structure; this doesn't defend against a malformed
 * `parentCommentId` chain, only against one that's simply missing (treated
 * as a root, rather than thrown away).
 */
export function buildCommentTree(flat: FlatComment[]): CommentNode[] {
  const byId = new Map<string, CommentNode>()
  for (const comment of flat) {
    byId.set(comment.id, { ...comment, children: [] })
  }

  const roots: CommentNode[] = []
  for (const comment of flat) {
    const node = byId.get(comment.id)!
    const parent = comment.parentCommentId ? byId.get(comment.parentCommentId) : undefined
    if (parent) {
      parent.children.push(node)
    } else {
      roots.push(node)
    }
  }

  const sortByCreatedAt = (nodes: CommentNode[]): void => {
    nodes.sort((a, b) => a.createdAtUtc.localeCompare(b.createdAtUtc))
    for (const node of nodes) {
      sortByCreatedAt(node.children)
    }
  }
  sortByCreatedAt(roots)

  return roots
}
