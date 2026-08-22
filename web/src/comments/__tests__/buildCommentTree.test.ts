import { describe, expect, it } from 'vitest'
import { buildCommentTree, type FlatComment } from '../buildCommentTree'

function comment(overrides: Partial<FlatComment> & Pick<FlatComment, 'id'>): FlatComment {
  return {
    parentCommentId: null,
    body: 'body',
    isDeleted: false,
    authorUserId: 'user-ada',
    authorDisplayName: 'Ada',
    createdAtUtc: '2026-01-01T00:00:00Z',
    editedAtUtc: null,
    ...overrides,
  }
}

describe('buildCommentTree', () => {
  it('returns an empty tree for no comments', () => {
    expect(buildCommentTree([])).toEqual([])
  })

  it('builds a flat list of roots when nothing has a parent', () => {
    const flat = [comment({ id: 'a', createdAtUtc: '2026-01-01T00:00:01Z' }), comment({ id: 'b', createdAtUtc: '2026-01-01T00:00:02Z' })]
    const tree = buildCommentTree(flat)
    expect(tree.map((n) => n.id)).toEqual(['a', 'b'])
    expect(tree.every((n) => n.children.length === 0)).toBe(true)
  })

  it('nests a reply under its parent', () => {
    const flat = [
      comment({ id: 'root', createdAtUtc: '2026-01-01T00:00:00Z' }),
      comment({ id: 'reply', parentCommentId: 'root', createdAtUtc: '2026-01-01T00:01:00Z' }),
    ]
    const tree = buildCommentTree(flat)
    expect(tree).toHaveLength(1)
    expect(tree[0]!.children.map((c) => c.id)).toEqual(['reply'])
  })

  it('nests multiple levels deep', () => {
    const flat = [
      comment({ id: 'a', createdAtUtc: '2026-01-01T00:00:00Z' }),
      comment({ id: 'b', parentCommentId: 'a', createdAtUtc: '2026-01-01T00:01:00Z' }),
      comment({ id: 'c', parentCommentId: 'b', createdAtUtc: '2026-01-01T00:02:00Z' }),
    ]
    const tree = buildCommentTree(flat)
    expect(tree[0]!.children[0]!.children[0]!.id).toBe('c')
  })

  it('sorts siblings oldest-first at every level', () => {
    const flat = [
      comment({ id: 'root', createdAtUtc: '2026-01-01T00:00:00Z' }),
      comment({ id: 'later-reply', parentCommentId: 'root', createdAtUtc: '2026-01-01T00:05:00Z' }),
      comment({ id: 'earlier-reply', parentCommentId: 'root', createdAtUtc: '2026-01-01T00:01:00Z' }),
    ]
    const tree = buildCommentTree(flat)
    expect(tree[0]!.children.map((c) => c.id)).toEqual(['earlier-reply', 'later-reply'])
  })

  it('treats a comment whose parent is missing from the list as a root, rather than dropping it', () => {
    const flat = [comment({ id: 'orphan', parentCommentId: 'does-not-exist' })]
    const tree = buildCommentTree(flat)
    expect(tree.map((n) => n.id)).toEqual(['orphan'])
  })

  it('keeps a deleted comment as a tombstone node so its replies keep their parent', () => {
    const flat = [
      comment({ id: 'root', body: '', isDeleted: true, createdAtUtc: '2026-01-01T00:00:00Z' }),
      comment({ id: 'reply', parentCommentId: 'root', createdAtUtc: '2026-01-01T00:01:00Z' }),
    ]
    const tree = buildCommentTree(flat)
    expect(tree).toHaveLength(1)
    expect(tree[0]).toMatchObject({ id: 'root', isDeleted: true, body: '' })
    expect(tree[0]!.children.map((c) => c.id)).toEqual(['reply'])
  })
})
