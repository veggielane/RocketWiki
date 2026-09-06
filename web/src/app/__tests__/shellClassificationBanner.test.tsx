import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { RouterProvider, createMemoryRouter, Link } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { AppShell } from '../AppShell'
import { ColorModeProvider } from '../../theme/ColorModeProvider'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { FakePresenceTransport } from '../../realtime/FakePresenceTransport'
import { useClassificationBanner, type ClassificationBannerMarking } from '../../markings/classificationBannerContext'

/**
 * The classification banner is a row of the shell (design.md §21, ICDS's
 * single bottom-of-viewport banner), declared by the screen and rendered by
 * the frame. Two things have to hold for that to be a protective marking
 * rather than a decoration: it says what the screen on show is classified
 * as, and it stops saying so the moment that screen is gone. The layout half
 * — that it is the frame's last row, outside the scroll region, so nothing
 * can be underneath it — is what the browser a11y tier's page-edit captures
 * turned on, and is pinned here in DOM terms.
 */
vi.mock('react-oidc-context', () => ({
  useAuth: () => ({ user: { profile: { name: 'Viewer' } }, isAuthenticated: true, signoutRedirect: vi.fn() }),
}))

// One transport per test, never one per call: the shell asks for it on every
// render, and a fresh instance each time would re-subscribe presence on every
// render — an update loop, and nothing to do with the banner under test.
const holder = vi.hoisted(() => ({ transport: undefined as unknown }))
vi.mock('../../realtime/transports', () => ({
  getDefaultPresenceTransport: () => holder.transport,
  getDefaultNotificationsTransport: () => ({
    connect: () => Promise.resolve(),
    disconnect: () => Promise.resolve(),
    onNotification: () => () => {},
  }),
}))

beforeEach(() => {
  holder.transport = new FakePresenceTransport()
})

const SCOPE = 'Protective marking for this page'
const SECRET: ClassificationBannerMarking = { label: 'UK SECRET UK/US EYES ONLY', level: 'SECRET', scopeLabel: SCOPE }
const OFFICIAL: ClassificationBannerMarking = { label: 'UK OFFICIAL', level: 'OFFICIAL', scopeLabel: SCOPE }

/** A page screen whose marking has arrived. */
function MarkedScreen({ marking }: { marking: ClassificationBannerMarking }) {
  useClassificationBanner(marking)
  return (
    <>
      <div>marked screen</div>
      <Nav />
    </>
  )
}

/** A page screen still waiting for its page, then reclassified by hand. */
function LoadingScreen() {
  const [marking, setMarking] = useState<ClassificationBannerMarking | null>(null)
  useClassificationBanner(marking)
  return (
    <>
      <div>loading screen</div>
      <button type="button" onClick={() => setMarking(OFFICIAL)}>
        page arrives
      </button>
      <button type="button" onClick={() => setMarking(SECRET)}>
        reclassify
      </button>
      <Nav />
    </>
  )
}

/** A screen with nothing marked on it — search, settings, the space list. */
function PlainScreen() {
  return (
    <>
      <div>plain screen</div>
      <Nav />
    </>
  )
}

/** In-router links, so a navigation is a real one rather than a remount. */
function Nav() {
  return (
    <>
      <Link to="/search">go to search</Link>
      <Link to="/pages/p1">go to page</Link>
    </>
  )
}

function renderShell(initialEntry: string) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceList') return { spaces: [] }
    if (name === 'CurrentUser')
      return {
        me: { id: 'sub-1', email: null, name: 'Viewer', groups: [], isAuthenticated: true, isInstanceAdmin: false, localUserId: 'user-1' },
      }
    if (name === 'UnreadNotificationCount') return { unreadNotificationCount: 0 }
    return undefined
  })
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppShell />,
        children: [
          { path: 'search', element: <PlainScreen /> },
          { path: 'pages/:pageId', element: <MarkedScreen marking={SECRET} /> },
          { path: 'loading', element: <LoadingScreen /> },
        ],
      },
    ],
    { initialEntries: [initialEntry] },
  )
  render(
    <ColorModeProvider>
      <UrqlProvider value={mock.client}>
        <RouterProvider router={router} />
      </UrqlProvider>
    </ColorModeProvider>,
  )
}

const banner = () => document.querySelector('[data-classification-banner="foot"]')
const printHead = () => document.querySelector('[data-classification-banner="print-head"]')

describe('the shell renders the marking the screen declares', () => {
  it('as a landmark region naming what it marks, carrying the label verbatim', async () => {
    renderShell('/pages/p1')
    await screen.findByText('marked screen')
    // "This page" and "this answer" are different claims, and a reader who
    // cannot see where the banner sits has nothing else to tell them apart.
    const region = screen.getByRole('region', { name: SCOPE })
    expect(region).toBe(banner())
    expect(region.textContent).toBe(`${SCOPE}: UK SECRET UK/US EYES ONLY`)
  })

  it('as the frame’s last row: outside the scroll region, after it, and a direct child of the frame', async () => {
    renderShell('/pages/p1')
    await screen.findByText('marked screen')
    const main = screen.getByRole('main')
    const strip = banner()!
    // Not inside the scroll region — a strip inside it, fixed or sticky, is
    // exactly what floated over the editor's save bar and the last line of
    // every page. After it, so the region ends where the banner begins.
    expect(main.contains(strip)).toBe(false)
    expect(main.compareDocumentPosition(strip) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    // A direct child of the frame's column (main sits in the row inside it),
    // which the print-only head copy's `order` depends on.
    const frame = main.parentElement!.parentElement!
    expect(strip.parentElement).toBe(frame)
    expect(frame.lastElementChild).toBe(printHead())
  })

  it('keeps the print-only head copy: hidden on screen, the same label, never announced twice', async () => {
    renderShell('/pages/p1')
    await screen.findByText('marked screen')
    const head = printHead()!
    expect(head.getAttribute('aria-hidden')).toBe('true')
    expect(head.textContent).toBe('UK SECRET UK/US EYES ONLY')
    expect(screen.getAllByRole('region', { name: SCOPE })).toHaveLength(1)
  })

  it('shows nothing on a screen that declares nothing', async () => {
    renderShell('/search')
    await screen.findByText('plain screen')
    expect(screen.queryByRole('region', { name: SCOPE })).toBeNull()
    expect(document.querySelector('[data-classification-banner]')).toBeNull()
  })

  it('drops the banner when you leave a marked screen for an unmarked one', async () => {
    renderShell('/pages/p1')
    await screen.findByText('marked screen')
    expect(banner()).not.toBeNull()

    fireEvent.click(screen.getByRole('link', { name: 'go to search' }))

    await screen.findByText('plain screen')
    // Gone with the screen, not merely stale: a SECRET strip under the search
    // results would be a wrong marking, and a screenshot cannot tell.
    expect(banner()).toBeNull()
    expect(printHead()).toBeNull()
  })

  it('appears only once the screen has a marking to declare, and follows it as it changes', async () => {
    renderShell('/loading')
    await screen.findByText('loading screen')
    expect(banner()).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: 'page arrives' }))
    await waitFor(() => expect(banner()?.textContent).toBe(`${SCOPE}: UK OFFICIAL`))

    fireEvent.click(screen.getByRole('button', { name: 'reclassify' }))
    await waitFor(() => expect(banner()?.textContent).toBe(`${SCOPE}: UK SECRET UK/US EYES ONLY`))
    expect(screen.getAllByRole('region', { name: SCOPE })).toHaveLength(1)
  })
})
