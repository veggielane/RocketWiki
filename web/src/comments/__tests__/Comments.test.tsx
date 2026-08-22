import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { Comments } from '../Comments'
import type { FlatComment } from '../buildCommentTree'

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

/**
 * Deleting a comment is restricted to its own author, or someone who can
 * manage this page's access (space-admin/instance-admin) — not any
 * commenter. A comment section that lets anyone delete anyone else's post
 * is a moderation hole, so this pins the rule down directly.
 */
describe('Comments — delete authorization', () => {
  it('shows Delete on your own comment', () => {
    render(
      <Comments
        pageId="page-1"
        comments={[comment({ id: 'c1', authorUserId: 'me' })]}
        canComment
        currentUserId="me"
        canManageAccess={false}
        onAdd={vi.fn()}
        onDelete={vi.fn()}
      />,
    )
    expect(screen.getByRole('button', { name: 'Delete' })).toBeInTheDocument()
  })

  it('hides Delete on someone else\'s comment when you cannot manage access', () => {
    render(
      <Comments
        pageId="page-1"
        comments={[comment({ id: 'c1', authorUserId: 'someone-else' })]}
        canComment
        currentUserId="me"
        canManageAccess={false}
        onAdd={vi.fn()}
        onDelete={vi.fn()}
      />,
    )
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument()
  })

  it('shows Delete on someone else\'s comment when you can manage access', () => {
    render(
      <Comments
        pageId="page-1"
        comments={[comment({ id: 'c1', authorUserId: 'someone-else' })]}
        canComment
        currentUserId="me"
        canManageAccess={true}
        onAdd={vi.fn()}
        onDelete={vi.fn()}
      />,
    )
    expect(screen.getByRole('button', { name: 'Delete' })).toBeInTheDocument()
  })

  it('never shows Delete or Reply on an already-deleted (tombstone) comment', () => {
    render(
      <Comments
        pageId="page-1"
        comments={[comment({ id: 'c1', authorUserId: 'me', isDeleted: true, body: '' })]}
        canComment
        currentUserId="me"
        canManageAccess={true}
        onAdd={vi.fn()}
        onDelete={vi.fn()}
      />,
    )
    expect(screen.getByText('This comment was deleted.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reply' })).not.toBeInTheDocument()
  })

  it('a deleted comment keeps its reply visible (tombstone preserves thread shape)', () => {
    render(
      <Comments
        pageId="page-1"
        comments={[
          comment({ id: 'root', isDeleted: true, body: '' }),
          comment({ id: 'reply', parentCommentId: 'root', authorDisplayName: 'Grace' }),
        ]}
        canComment
        currentUserId="me"
        canManageAccess={false}
        onAdd={vi.fn()}
        onDelete={vi.fn()}
      />,
    )
    expect(screen.getByText('This comment was deleted.')).toBeInTheDocument()
    expect(screen.getByText('Grace')).toBeInTheDocument()
  })

  it('hides the composer and all Reply buttons when canComment is false', () => {
    render(
      <Comments
        pageId="page-1"
        comments={[comment({ id: 'c1' })]}
        canComment={false}
        currentUserId="me"
        canManageAccess={false}
        onAdd={vi.fn()}
        onDelete={vi.fn()}
      />,
    )
    expect(screen.queryByRole('button', { name: 'Post comment' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reply' })).not.toBeInTheDocument()
  })
})
