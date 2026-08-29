import { describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
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
  /** Widened so an override can stage a name from a newer icon set (design.md §12). */
  icon: null as string | null,
  content: 'Hello world.\n',
  currentRevisionNumber: 3,
  canEdit: false,
  canComment: false,
  canManageAccess: false,
  viewerIsWatching: false,
  labels: ['ops'],
  labelDetails: [{ id: 'l-ops', spaceId: 'space-1', name: 'ops' }],
  // design.md §21.5: there is no unmarked state, so every staged page carries
  // one. `label` is the server's own formatting — the SPA renders it verbatim.
  // Widened so an override can stage another level, a caveat, or §21.12's
  // legal no-prefix marking.
  marking: {
    level: 'OFFICIAL' as string,
    levelName: 'OFFICIAL' as string,
    eyesOnly: [] as string[],
    prefix: 'UK' as string | null,
    label: 'UK OFFICIAL',
  },
  properties: [
    { keyId: 'k-owner', key: 'Owner', value: 'Propulsion team', sortOrder: 0 },
    { keyId: 'k-status', key: 'Status', value: 'Draft', sortOrder: 1 },
  ],
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

describe('PageViewPage page icon', () => {
  it('shows the page icon beside the title without adding it to the heading', async () => {
    renderPage({ pageOverrides: { icon: 'ROCKET' } })
    // The heading's accessible name stays the title alone — the glyph sits
    // outside it and is decorative, so it is never read as part of the name.
    const heading = await screen.findByRole('heading', { name: 'Runbook' })
    expect(heading).toHaveTextContent('Runbook')
    expect(screen.getByTestId('RocketLaunchOutlinedIcon')).toBeInTheDocument()
  })

  it('draws nothing beside the title for a page with no icon', async () => {
    // Unlike a tree row, a heading has no neighbours to stay aligned with, so
    // a generic glyph here would decorate every page and say nothing.
    renderPage()
    await screen.findByRole('heading', { name: 'Runbook' })
    expect(screen.queryByTestId('ArticleOutlinedIcon')).toBeNull()
  })

  it('draws nothing for an icon this build has never heard of', async () => {
    renderPage({ pageOverrides: { icon: 'SATELLITE' } })
    await screen.findByRole('heading', { name: 'Runbook' })
    expect(screen.queryByTestId('ArticleOutlinedIcon')).toBeNull()
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

describe('PageViewPage properties panel (design.md §20)', () => {
  it('reads properties as a definition list, not free text, for any viewer', async () => {
    renderPage()
    const owner = await screen.findByText('Owner')
    expect(owner.tagName).toBe('DT')
    expect(screen.getByText('Propulsion team').tagName).toBe('DD')
    expect(screen.getByText('Status').tagName).toBe('DT')
    expect(screen.getByText('Draft').tagName).toBe('DD')
  })

  it('offers the properties screen only with canEdit', async () => {
    renderPage()
    await screen.findByText('Owner')
    expect(screen.queryByRole('link', { name: /properties/i })).not.toBeInTheDocument()
  })

  it('links an editor through to the properties screen', async () => {
    renderPage({ pageOverrides: { canEdit: true } })
    expect(await screen.findByRole('link', { name: 'Edit properties' })).toHaveAttribute(
      'href',
      '/pages/page-1/properties',
    )
  })

  it('offers an editor the screen even with no properties yet, and shows a viewer nothing', async () => {
    renderPage({ pageOverrides: { canEdit: true, properties: [] } })
    expect(await screen.findByRole('link', { name: 'Add properties' })).toBeInTheDocument()
    cleanup()
    renderPage({ pageOverrides: { properties: [] } })
    await screen.findByRole('heading', { name: 'Runbook' })
    expect(screen.queryByRole('heading', { name: 'Properties' })).not.toBeInTheDocument()
  })
})

describe('PageViewPage protective marking (design.md §21)', () => {
  it('shows the marking at the top AND the bottom, both the same string', async () => {
    renderPage({
      pageOverrides: {
        marking: {
          level: 'SECRET',
          levelName: 'SECRET',
          eyesOnly: ['UK', 'US'],
          prefix: 'UK',
          label: 'UK SECRET [UK/US EYES ONLY]',
        },
      },
    })
    await screen.findByRole('heading', { name: 'Runbook' })
    // ICDS: ONE banner, fixed to the bottom of the viewport, so the marking
    // stays on screen while scrolling instead of bracketing the content. The
    // reason the old top-and-bottom pair existed — someone printing a long page
    // has to meet the marking without knowing where to scroll — is kept as a
    // print-only copy at the head of the document.
    const banners = document.querySelectorAll('[data-classification-banner]')
    expect([...banners].map((b) => b.getAttribute('data-classification-banner'))).toEqual([
      'fixed',
      'print-head',
    ])
    for (const banner of banners) {
      expect(banner.textContent).toContain('UK SECRET [UK/US EYES ONLY]')
    }
  })

  it('announces the fixed banner as a landmark naming what it marks', async () => {
    // "This page" and "this answer" are different claims, and a reader who
    // cannot see where the banner sits has nothing else to tell them apart.
    renderPage()
    await screen.findByRole('heading', { name: 'Runbook' })
    expect(
      screen.getByRole('region', { name: 'Protective marking for this page' }),
    ).toBeInTheDocument()
  })

  it("renders the server's label rather than composing prefix + level + caveat", async () => {
    // A marking with no prefix reads as the bare level, with no leading space
    // and no "UK" invented for it (design.md §21.12).
    renderPage({
      pageOverrides: {
        marking: { level: 'TOP_SECRET', levelName: 'TOP SECRET', eyesOnly: [], prefix: null, label: 'TOP SECRET' },
      },
    })
    await screen.findByRole('heading', { name: 'Runbook' })
    expect(document.querySelector('[data-classification-banner="fixed"]')?.textContent).toBe(
      'Protective marking for this page: TOP SECRET',
    )
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
