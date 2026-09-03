import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { SlugPageRoute } from '../SlugPageRoute'
import { createMockUrqlClient } from '../../test/mockUrqlClient'

// This route renders the real page view, which joins presence on mount — so it
// inherits that view's rule about never constructing a SignalR connection here.
vi.mock('../../realtime/transports', async () => {
  const { FakePresenceTransport } = await import('../../realtime/FakePresenceTransport')
  const transport = new FakePresenceTransport()
  return { getDefaultPresenceTransport: () => transport }
})

const page = {
  id: 'page-1',
  spaceId: 'space-1',
  spaceKey: 'ENG',
  title: 'Launch notes',
  slug: 'launch-notes',
  icon: null,
  content: 'Ship on Friday.\n',
  currentRevisionNumber: 1,
  canEdit: false,
  canComment: false,
  canManageAccess: false,
  viewerIsWatching: false,
  labels: [] as string[],
  labelDetails: [] as unknown[],
  properties: [] as unknown[],
  marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], ukPrefix: true, selectors: [], label: 'UK OFFICIAL' },
  parent: null,
  parentDenial: null,
  linkTargets: [] as unknown[],
  children: [] as unknown[],
  comments: [] as unknown[],
  attachments: [] as unknown[],
}

const denial = {
  placeholderTitle: '(protected)',
  noSpaceAccess: false,
  marking: { level: 'SECRET', levelName: 'SECRET', eyesOnly: ['US'], ukPrefix: true, selectors: [], label: 'UK SECRET US EYES ONLY' },
  reasons: [{ gate: 'NATIONAL_CAVEAT', passed: false, countries: ['US'] }],
}

function renderRoute(
  path: string,
  respond: (name: string) => Record<string, unknown> | undefined,
) {
  const mock = createMockUrqlClient((name) => respond(name))
  render(
    <MemoryRouter initialEntries={[path]}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route path="/spaces/:spaceKey/:slug" element={<SlugPageRoute />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

describe('SlugPageRoute', () => {
  it('resolves the slug to an id and then renders the ordinary page view', async () => {
    const mock = renderRoute('/spaces/ENG/launch-notes', (name) => {
      if (name === 'PageAccessBySlug') return { pageAccessBySlug: { page: { id: 'page-1' }, denial: null } }
      if (name === 'PageAccessById') return { pageAccess: { page, denial: null } }
      return undefined
    })

    expect(await screen.findByRole('heading', { name: 'Launch notes' })).toBeInTheDocument()

    // The resolution is deliberately id-only and the page is then read exactly as
    // /pages/{id} reads it — one page-rendering path, not two that could drift.
    const bySlug = mock.operations.find((op) => op.name === 'PageAccessBySlug')
    expect(bySlug?.variables).toEqual({ spaceKey: 'ENG', slug: 'launch-notes' })
    expect(mock.operations.some((op) => op.name === 'PageAccessById')).toBe(true)
  })

  it('renders the protected screen for a page this caller may not read, without a second read', async () => {
    // design.md §6.7 / §21.8: the address is honest about what sits at it —
    // the marking and the reason, never the title.
    const mock = renderRoute('/spaces/ENG/withheld', (name) =>
      name === 'PageAccessBySlug' ? { pageAccessBySlug: { page: null, denial } } : undefined,
    )

    expect(await screen.findByRole('heading', { level: 1, name: 'Protected page' })).toBeInTheDocument()
    expect(screen.getByText('UK SECRET US EYES ONLY')).toBeInTheDocument()
    expect(screen.getByText('Releasable to US only.')).toBeInTheDocument()
    expect(document.body.textContent).not.toContain('Launch notes')
    expect(mock.operations.some((op) => op.name === 'PageAccessById')).toBe(false)
  })

  it('shows the not-found state for a slug nobody has', async () => {
    renderRoute('/spaces/ENG/no-such-page', (name) =>
      name === 'PageAccessBySlug' ? { pageAccessBySlug: null } : undefined,
    )

    expect(await screen.findByText("Couldn't load this page.")).toBeInTheDocument()
    expect(screen.queryByText('Protected page')).toBeNull()
  })
})
