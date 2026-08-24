import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { PageViewPage } from '../PageViewPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

// The page view joins presence on mount; tests must never construct a real
// SignalR connection (design.md §8 — and jsdom has no hub to reach).
vi.mock('../../realtime/transports', async () => {
  const { FakePresenceTransport } = await import('../../realtime/FakePresenceTransport')
  const transport = new FakePresenceTransport()
  return { getDefaultPresenceTransport: () => transport }
})

const basePage = {
  id: 'page-1',
  spaceId: 'space-1',
  spaceKey: 'ENG',
  title: 'Runbook',
  slug: 'runbook',
  content: 'Hello world.\n',
  currentRevisionNumber: 3,
  canEdit: false,
  canComment: false,
  canManageAccess: false,
  viewerIsWatching: false,
  labels: ['ops'],
  labelDetails: [{ id: 'l-ops', spaceId: 'space-1', name: 'ops' }],
  parent: null,
  children: [],
  comments: [
    {
      id: 'c1',
      parentCommentId: null,
      body: 'First comment.',
      isDeleted: false,
      authorUserId: 'user-ada',
      author: { id: 'user-ada', displayName: 'Ada Lovelace' },
      createdAtUtc: '2026-08-01T00:00:00Z',
      editedAtUtc: null,
    },
  ],
  attachments: [
    {
      id: 'att-1',
      fileName: 'spec.pdf',
      contentType: 'application/pdf',
      sizeBytes: 2048,
      uploadedBy: { id: 'user-grace', displayName: 'Grace Hopper' },
    },
  ],
}

function renderPage({
  pageOverrides = {} as Partial<typeof basePage>,
  isReplica = false,
  localUserId = 'user-someone-else' as string | null,
} = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'PageById') return { page: { ...basePage, ...pageOverrides } }
    if (name === 'CurrentUser')
      return {
        me: {
          id: 'sub-1',
          email: null,
          name: 'Viewer',
          groups: [],
          isAuthenticated: true,
          isInstanceAdmin: false,
          localUserId,
        },
      }
    if (name === 'SpaceReplicaBanner')
      return { space: { id: 'space-1', key: 'ENG', isReplica, originInstanceId: isReplica ? 'LOW' : 'HIGH' } }
    if (name === 'SpaceTreeForMove') return { pageTree: [] }
    if (name === 'SpaceLabelDetails') return { labelDetails: [] }
    return undefined
  })
  render(
    <MemoryRouter initialEntries={['/pages/page-1']}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route path="/pages/:pageId" element={<PageViewPage />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

/**
 * Permissions shape the UI (design.md §6): the server-computed
 * canEdit/canComment/canManageAccess fields decide what's offered, so the
 * page never offers what the server would refuse.
 */
describe('PageViewPage accessibility', () => {
  it('has no axe violations with full edit affordances, comments, and attachments', async () => {
    renderPage({ pageOverrides: { canEdit: true, canComment: true, canManageAccess: true } })
    await screen.findByRole('heading', { name: 'Runbook' })
    await screen.findByRole('button', { name: 'Move' })
    await expectNoAxeViolations()
  })
})

describe('PageViewPage permission-driven affordances', () => {
  it('hides Move/Edit/Delete/Permissions and the composer from a viewer-only user', async () => {
    renderPage()
    expect(await screen.findByRole('heading', { name: 'Runbook' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Move' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /edit/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /permissions/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Post comment' })).not.toBeInTheDocument()
    // The watch toggle and self-inspection stay available to any viewer.
    expect(screen.getByRole('button', { name: 'Watch' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Why can I see this page?' })).toBeInTheDocument()
  })

  it('offers Move/Edit/Delete and label editing when canEdit is true', async () => {
    renderPage({ pageOverrides: { canEdit: true } })
    expect(await screen.findByRole('button', { name: 'Move' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Edit' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Delete' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Edit labels' })).toBeInTheDocument()
    // Still no Permissions link — managing access is a separate right.
    expect(screen.queryByRole('link', { name: 'Permissions' })).not.toBeInTheDocument()
  })

  it('offers the Permissions link only with canManageAccess', async () => {
    renderPage({ pageOverrides: { canManageAccess: true } })
    expect(await screen.findByRole('link', { name: 'Permissions' })).toHaveAttribute(
      'href',
      '/pages/page-1/permissions',
    )
  })

  it('shows the composer when canComment is true', async () => {
    renderPage({ pageOverrides: { canComment: true } })
    expect(await screen.findByRole('button', { name: 'Post comment' })).toBeInTheDocument()
  })
})

describe('PageViewPage server-resolved read state', () => {
  it('renders comment authors and attachment uploaders by display name, not id stand-ins', async () => {
    renderPage()
    expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument()
    // The uploader line now interleaves an avatar between "uploaded by"
    // and the name (design.md §19) — the two text runs are siblings.
    expect(screen.getByText(/uploaded by/)).toBeInTheDocument()
    expect(screen.getByText('Grace Hopper')).toBeInTheDocument()
    expect(screen.queryByText(/User user-ada/)).not.toBeInTheDocument()
  })

  it('initializes the watch toggle from Page.viewerIsWatching', async () => {
    renderPage({ pageOverrides: { viewerIsWatching: true } })
    const button = await screen.findByRole('button', { name: 'Watching' })
    expect(button).toHaveAttribute('aria-pressed', 'true')
  })

  it('shows the delete-own affordance when localUserId matches the comment author', async () => {
    renderPage({ pageOverrides: { canComment: true }, localUserId: 'user-ada' })
    expect(await screen.findByRole('button', { name: 'Delete' })).toBeInTheDocument()
  })

  it('hides delete on someone else\'s comment without canManageAccess', async () => {
    renderPage({ pageOverrides: { canComment: true }, localUserId: 'user-not-ada' })
    await screen.findByText('Ada Lovelace')
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument()
  })
})

describe('PageViewPage replica banner (design.md §12)', () => {
  it('proactively shows "Replica of {origin} — read-only" on a replica space', async () => {
    renderPage({ isReplica: true })
    expect(await screen.findByText(/Replica of LOW — read-only/)).toBeInTheDocument()
  })

  it('shows no banner on a local space', async () => {
    renderPage()
    await screen.findByRole('heading', { name: 'Runbook' })
    expect(screen.queryByText(/Replica of/)).not.toBeInTheDocument()
  })
})
