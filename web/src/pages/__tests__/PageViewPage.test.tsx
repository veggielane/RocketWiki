import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { PageViewPage } from '../PageViewPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { FakePresenceTransport } from '../../realtime/FakePresenceTransport'
import { PresenceRoomContext } from '../../presence/PresenceRoomContext'
import type { PresenceViewer } from '../../realtime/types'
import { expectNoAxeViolations } from '../../test/axe'

// The page view joins presence on mount; tests must never construct a real
// SignalR connection (design.md §8 — and jsdom has no hub to reach). A fresh
// instance per test keeps recorded pointer traffic from leaking across them —
// same shape as the edit page's harness.
const holder = vi.hoisted(() => ({ transport: undefined as unknown }))
vi.mock('../../realtime/transports', () => ({
  getDefaultPresenceTransport: () => holder.transport,
}))

let transport: FakePresenceTransport

beforeEach(() => {
  transport = new FakePresenceTransport()
  holder.transport = transport
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
    ukPrefix: true as boolean, selectors: [] as { category: string; value: string }[],
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

/** A denial the disclosing read can answer instead of a page (design.md §6.7 / §21.8). */
const denial: {
  placeholderTitle: string
  noSpaceAccess: boolean
  marking: Record<string, unknown> | null
  reasons: Record<string, unknown>[]
} = {
  placeholderTitle: '(protected)',
  noSpaceAccess: false,
  marking: { level: 'SECRET', levelName: 'SECRET', eyesOnly: [], ukPrefix: true, selectors: [{ category: 'FRUIT', value: 'APPLE' }], label: 'UK SECRET APPLE' },
  reasons: [
    { gate: 'SELECTOR_GRANT', passed: false, category: 'FRUIT', value: 'APPLE' },
  ],
}

function renderPage({
  pageOverrides = {} as Partial<typeof basePage>,
  /** What `pageAccess` answers instead of the staged page: a denial, or null for no such page. */
  access = undefined as { page: null; denial: typeof denial | null } | null | undefined,
  isReplica = false,
  localUserId = 'user-someone-else' as string | null,
  presence = { viewers: [] as PresenceViewer[], setRoom: () => {} },
} = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'PageAccessById')
      return {
        pageAccess:
          access !== undefined
            ? access
            : { page: { ...basePage, ...pageOverrides, parentDenial: null, linkTargets: [] }, denial: null },
      }
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
        {/* Stands in for the shell, which owns presence and receives the room
            this screen declares. */}
        <PresenceRoomContext value={presence}>
          <Routes>
            <Route path="/pages/:pageId" element={<PageViewPage />} />
          </Routes>
        </PresenceRoomContext>
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
/**
 * The secondary page actions (History, Details, Add child page, Move,
 * Permissions, Delete) live behind one overflow menu rather than in a row of
 * eight equally-weighted buttons — see PageViewPage's own comment. Tests that
 * are about *which* actions are offered have to open it first.
 */
async function openMoreActions() {
  fireEvent.click(await screen.findByRole('button', { name: 'More actions' }))
  return screen.findByRole('menu')
}

describe('PageViewPage accessibility', () => {
  it('has no axe violations with full edit affordances, comments, and attachments', async () => {
    renderPage({ pageOverrides: { canEdit: true, canComment: true, canManageAccess: true } })
    await screen.findByRole('heading', { name: 'Runbook' })
    // With the menu OPEN — the affordances it holds are otherwise unrendered
    // and would drop out of the sweep entirely.
    await openMoreActions()
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
    expect(screen.queryByRole('menuitem', { name: 'Move' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /edit/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /permissions/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Post comment' })).not.toBeInTheDocument()
    // The watch toggle and self-inspection stay available to any viewer.
    expect(screen.getByRole('button', { name: 'Watch' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Why can I see this page?' })).toBeInTheDocument()
  })

  it('offers Edit, Delete and label editing when canEdit is true', async () => {
    renderPage({ pageOverrides: { canEdit: true } })
    // Edit is the one action promoted out of the menu: it is what most readers
    // came to do, and it is the screen's only emphasised button.
    expect(await screen.findByRole('link', { name: 'Edit' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Edit labels' })).toBeInTheDocument()
    await openMoreActions()
    expect(screen.getByRole('menuitem', { name: 'Delete' })).toBeInTheDocument()
  })

  it('offers neither Move nor Permissions here — both moved to the details screen', async () => {
    // Even with every right. The page view stopped being where unrelated page
    // management accretes; Details is the single door to it.
    renderPage({ pageOverrides: { canEdit: true, canManageAccess: true } })
    await openMoreActions()
    expect(screen.queryByRole('menuitem', { name: 'Move' })).not.toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: 'Permissions' })).not.toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: 'Details' })).toBeInTheDocument()
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
  it('renders no properties panel — they live on the details screen alone now', async () => {
    // Two renderings of the same rows, one of them wedged above the content,
    // was the clutter this move removed. Properties are still readable by any
    // viewer; the details screen is where.
    renderPage()
    await screen.findByRole('heading', { name: 'Runbook' })
    expect(screen.queryByText('Owner')).not.toBeInTheDocument()
    expect(screen.queryByText('Propulsion team')).not.toBeInTheDocument()
  })

  it('sends an editor to the details screen, not to a properties one', async () => {
    // Managing properties moved off the page. The link an editor gets is to
    // everything-about-the-page, which is where the rest of the chrome is headed.
    renderPage({ pageOverrides: { canEdit: true } })
    await openMoreActions()
    expect(screen.getByRole('menuitem', { name: 'Details' })).toHaveAttribute(
      'href',
      '/pages/page-1/details',
    )
  })

  it('offers no Details link to someone who cannot edit', async () => {
    // The screen refuses non-editors, so offering it would be an invitation to a
    // refusal - the same "hidden rather than offered-and-refused" rule the edit and
    // move affordances already follow.
    renderPage({ pageOverrides: { canEdit: false } })
    await screen.findByRole('heading', { name: 'Runbook' })
    await openMoreActions()
    expect(screen.queryByRole('menuitem', { name: 'Details' })).not.toBeInTheDocument()
  })

  it('offers History to a reader who cannot edit, unlike Details', async () => {
    // The two links sit side by side and are gated differently on purpose:
    // Details is an editor screen, History is provenance for content this
    // reader is already looking at, so hiding it would withhold nothing they
    // could not reconstruct from the page in front of them.
    renderPage({ pageOverrides: { canEdit: false } })
    await openMoreActions()
    expect(screen.getByRole('menuitem', { name: 'History' })).toHaveAttribute(
      'href',
      '/pages/page-1/history',
    )
  })

  it('shows the panel only when there is something in it', async () => {
    // Previously an editor saw an empty panel carrying an "Add properties" link, so
    // every page wore a box whether or not it had any properties. The panel is now
    // purely a display of values that exist.
    renderPage({ pageOverrides: { canEdit: true, properties: [] } })
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
          ukPrefix: true, selectors: [],
          label: 'UK SECRET UK/US EYES ONLY',
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
      expect(banner.textContent).toContain('UK SECRET UK/US EYES ONLY')
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
        marking: { level: 'TOP_SECRET', levelName: 'TOP SECRET', eyesOnly: [], ukPrefix: false, selectors: [], label: 'TOP SECRET' },
      },
    })
    await screen.findByRole('heading', { name: 'Runbook' })
    expect(document.querySelector('[data-classification-banner="fixed"]')?.textContent).toBe(
      'Protective marking for this page: TOP SECRET',
    )
  })
})

describe('PageViewPage withheld and missing pages (design.md §6.7 / §21.8)', () => {
  it('renders the protected screen for a denial — marking and reasons, and never the title', async () => {
    renderPage({ access: { page: null, denial } })
    expect(await screen.findByRole('heading', { level: 1, name: 'Protected page' })).toBeInTheDocument()
    expect(screen.getByText('UK SECRET APPLE')).toBeInTheDocument()
    expect(screen.getByText('APPLE is not granted to you in this space.')).toBeInTheDocument()
    // Nothing of the page itself: no title, no content, no actions.
    expect(document.body.textContent).not.toContain('Runbook')
    expect(document.body.textContent).not.toContain('Hello world')
    expect(screen.queryByRole('button', { name: 'Watch' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'More actions' })).toBeNull()
  })

  it('renders only the space sentence when the caller holds no access grant in the space', async () => {
    renderPage({
      access: { page: null, denial: { ...denial, noSpaceAccess: true, marking: null, reasons: [{ gate: 'SPACE_ACCESS', passed: false }] } },
    })
    expect(await screen.findByText('You have no access to this space.')).toBeInTheDocument()
    expect(screen.queryByText(/SECRET/)).toBeNull()
  })

  it('renders the not-found notice for a page that does not exist', async () => {
    renderPage({ access: null })
    expect(await screen.findByText("Couldn't load this page.")).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Protected page' })).toBeNull()
  })

  it('substitutes the caller\'s own fallback for a denial as well as for nothing', async () => {
    // The space-home route wants a withheld default page to behave like no
    // default page — the browser, not the protected screen.
    const mock = createMockUrqlClient((name) =>
      name === 'PageAccessById' ? { pageAccess: { page: null, denial } } : undefined,
    )
    render(
      <MemoryRouter initialEntries={['/pages/page-1']}>
        <UrqlProvider value={mock.client}>
          <PresenceRoomContext value={{ viewers: [], setRoom: () => {} }}>
            <Routes>
              <Route path="/pages/:pageId" element={<PageViewPage onUnavailable={<div>the browser instead</div>} />} />
            </Routes>
          </PresenceRoomContext>
        </UrqlProvider>
      </MemoryRouter>,
    )
    expect(await screen.findByText('the browser instead')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Protected page' })).toBeNull()
  })

  it('has no axe violations on the protected screen', async () => {
    renderPage({ access: { page: null, denial } })
    await screen.findByRole('heading', { level: 1, name: 'Protected page' })
    await expectNoAxeViolations()
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

/**
 * What the page screen still owns after presence moved to the shell.
 *
 * The cursors, the overlay and the hub traffic are the shell's now — it is the
 * only component that sees every route, and confining presence to two page
 * components is why fifteen other screens never had it. What cannot move is
 * WHICH page: the readable address carries a slug, not an id, so only the
 * screen that resolved it can name the room.
 */
describe('PageViewPage — declares its presence room', () => {
  it('names the room by page id, whichever URL got here', async () => {
    const setRoom = vi.fn()
    renderPage({ presence: { viewers: [], setRoom } })
    await screen.findByRole('heading', { name: 'Runbook' })

    // Not the path: /pages/page-1 and /spaces/ENG/runbook are the same screen
    // and must be the same room.
    expect(setRoom).toHaveBeenCalledWith('page:page-1')
  })

  it('renders the viewer avatars the shell hands down', async () => {
    renderPage({
      presence: {
        viewers: [{ userId: 'user-9', displayName: 'Zoe Zephyr', colour: 'hsl(9, 70%, 45%)', hasAvatar: false }],
        setRoom: vi.fn(),
      },
    })

    // The avatar is an identity graphic labelled with the display name.
    expect(await screen.findByRole('img', { name: 'Zoe Zephyr' })).toBeInTheDocument()
  })
})
