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
  marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], prefix: 'UK', label: 'UK OFFICIAL' },
  parent: null,
  children: [] as unknown[],
  comments: [] as unknown[],
  attachments: [] as unknown[],
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
      if (name === 'PageBySlug') return { pageBySlug: { id: 'page-1' } }
      if (name === 'PageById') return { page }
      return undefined
    })

    expect(await screen.findByRole('heading', { name: 'Launch notes' })).toBeInTheDocument()

    // The resolution is deliberately id-only and the page is then read exactly as
    // /pages/{id} reads it — one page-rendering path, not two that could drift.
    const bySlug = mock.operations.find((op) => op.name === 'PageBySlug')
    expect(bySlug?.variables).toEqual({ spaceKey: 'ENG', slug: 'launch-notes' })
    expect(mock.operations.some((op) => op.name === 'PageById')).toBe(true)
  })

  it('shows the same not-found state for a slug nobody has and one this caller may not view', async () => {
    // design.md §6.7: the server returns null for both, and the client must not
    // sharpen that back into a distinction — a URL that answered "exists but
    // forbidden" would be a way to ask whether a page exists.
    renderRoute('/spaces/ENG/no-such-page', (name) =>
      name === 'PageBySlug' ? { pageBySlug: null } : undefined,
    )

    expect(await screen.findByText("Couldn't load this page.")).toBeInTheDocument()
  })
})
