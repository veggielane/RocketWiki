import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { SpaceHomeRoute } from '../SpaceHomeRoute'
import { createMockUrqlClient } from '../../test/mockUrqlClient'

// The page view joins presence on mount, and this route renders the real one.
vi.mock('../../realtime/transports', async () => {
  const { FakePresenceTransport } = await import('../../realtime/FakePresenceTransport')
  const transport = new FakePresenceTransport()
  return { getDefaultPresenceTransport: () => transport }
})

const space = {
  id: 'space-1',
  key: 'ENG',
  name: 'Engineering',
  description: null,
  homepageId: null as string | null,
  isReplica: false,
  originInstanceId: 'HIGH',
  viewerIsWatching: false,
  grants: [] as { id: string }[],
}

const homepage = {
  id: 'page-1',
  spaceId: 'space-1',
  spaceKey: 'ENG',
  title: 'Engineering handbook',
  slug: 'handbook',
  icon: null,
  content: 'Start here.\n',
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

function renderHome({
  homepageId = null as string | null,
  pageResolves = true,
} = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceTree') return { space: { ...space, homepageId } }
    if (name === 'SpacePageTree') return { pageTree: [] }
    if (name === 'PageById') return { page: pageResolves ? homepage : null }
    return undefined
  })
  render(
    <MemoryRouter initialEntries={['/spaces/ENG']}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route path="/spaces/:spaceKey" element={<SpaceHomeRoute />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

describe('SpaceHomeRoute', () => {
  it('shows the space browser when no default page is set', async () => {
    renderHome({ homepageId: null })
    expect(await screen.findByRole('heading', { name: 'Engineering' })).toBeInTheDocument()
  })

  it('shows the default page when one is set', async () => {
    renderHome({ homepageId: 'page-1' })
    expect(await screen.findByRole('heading', { name: 'Engineering handbook' })).toBeInTheDocument()
  })

  it('falls back to the browser when the default page is not viewable', async () => {
    // design.md §6.7: the server collapses denied into absent, so a space whose
    // default page is above this caller's clearance must behave like a space
    // with no default page — never a dead end where the space used to be, and
    // never a hint that a page is there.
    renderHome({ homepageId: 'page-1', pageResolves: false })
    expect(await screen.findByRole('heading', { name: 'Engineering' })).toBeInTheDocument()
    expect(screen.queryByText("Couldn't load this page.")).toBeNull()
  })
})
