import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render as renderBare, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import type { ReactElement } from 'react'
import { Comments } from '../Comments'
import type { FlatComment } from '../buildCommentTree'

// Author bylines link to profiles (users/UserLink.tsx), and a link needs a
// router to render. Nothing here asserts on navigation.
const render = (ui: ReactElement) => renderBare(<MemoryRouter>{ui}</MemoryRouter>)

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

/**
 * The composer's keyboard path (Ctrl/Cmd+Enter — the shortcut mechanics
 * live in editor/__tests__/composerSubmitShortcut.test.tsx). What this
 * block pins is the announcement: the helper text is visible AND
 * programmatically associated (aria-describedby), the same pattern as
 * Ask's "Enter to ask" hint — an unannounced shortcut is no keyboard path.
 */
describe('Comments — composer keyboard submit hint', () => {
  it('the new-comment composer is described by the visible shortcut hint', () => {
    render(
      <Comments
        pageId="page-1"
        comments={[]}
        canComment
        currentUserId="me"
        canManageAccess={false}
        onAdd={vi.fn()}
        onDelete={vi.fn()}
      />,
    )
    const composer = screen.getByRole('textbox', { name: 'New comment' })
    expect(composer).toHaveAccessibleDescription(/Enter to post/)
    expect(screen.getByText(/Enter to post/)).toBeVisible()
  })

  it('the reply composer gets the same treatment', () => {
    render(
      <Comments
        pageId="page-1"
        comments={[comment({ id: 'c1' })]}
        canComment
        currentUserId="me"
        canManageAccess={false}
        onAdd={vi.fn()}
        onDelete={vi.fn()}
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Reply' }))
    const composer = screen.getByRole('textbox', { name: 'Reply to Ada' })
    expect(composer).toHaveAccessibleDescription(/Enter to post/)
  })
})

/**
 * Comment authors get a face (design.md §19): image only when the read's
 * `UserRef.hasAvatar` says so — never a probing GET per row — with the
 * initials chip as the unchanged fallback. The blob-fetch behavior itself
 * is covered in avatars/__tests__/UserAvatar.test.tsx; this pins the
 * wiring: the flag reaches the avatar, and absent means initials.
 */
describe('Comments — author avatars', () => {
  it('renders initials without fetching when the author has no avatar', () => {
    const fetchSpy = vi.fn()
    vi.stubGlobal('fetch', fetchSpy)
    try {
      render(
        <Comments
          pageId="page-1"
          comments={[comment({ id: 'c1', authorHasAvatar: false })]}
          canComment={false}
          currentUserId="me"
          canManageAccess={false}
          onAdd={vi.fn()}
          onDelete={vi.fn()}
        />,
      )
      expect(screen.getByText('A')).toBeInTheDocument() // initials chip
      expect(fetchSpy).not.toHaveBeenCalledWith('/users/user-ada/avatar', expect.anything())
    } finally {
      vi.unstubAllGlobals()
    }
  })

  it('fetches the avatar through the authenticated route when hasAvatar is true', async () => {
    const fetchSpy = vi.fn(async () => new Response(new Blob(['png']), { status: 200 }))
    vi.stubGlobal('fetch', fetchSpy)
    URL.createObjectURL = vi.fn(() => 'blob:ada-face')
    URL.revokeObjectURL = vi.fn()
    try {
      render(
        <Comments
          pageId="page-1"
          comments={[comment({ id: 'c1', authorHasAvatar: true })]}
          canComment={false}
          currentUserId="me"
          canManageAccess={false}
          onAdd={vi.fn()}
          onDelete={vi.fn()}
        />,
      )
      // findByAltText targets the <img> element itself — the avatar root is
      // also role="img" now (UserAvatar names it for Tooltip compatibility).
      const img = await screen.findByAltText('Ada')
      expect(img).toHaveAttribute('src', 'blob:ada-face')
      expect(fetchSpy).toHaveBeenCalledWith('/users/user-ada/avatar', expect.anything())
    } finally {
      vi.unstubAllGlobals()
      vi.restoreAllMocks()
    }
  })
})
