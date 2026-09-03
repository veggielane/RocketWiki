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
  canManageAccess: false,
  viewerHasAccess: true,
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
  marking: { level: 'TOP_SECRET', levelName: 'TOP SECRET', eyesOnly: [], ukPrefix: true, selectors: [], label: 'UK TOP SECRET' },
  reasons: [{ gate: 'CLASSIFICATION', passed: false, requiredLevelName: 'TOP SECRET' }],
}

function renderHome({
  homepageId = null as string | null,
  access = 'page' as 'page' | 'missing' | 'denied',
} = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceTree') return { space: { ...space, homepageId } }
    if (name === 'SpacePageTree') return { pageTree: [] }
    if (name === 'PageAccessById')
      return {
        pageAccess:
          access === 'page' ? { page: homepage, denial: null } : access === 'denied' ? { page: null, denial } : null,
      }
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

  it('falls back to the browser when the default page no longer exists', async () => {
    renderHome({ homepageId: 'page-1', access: 'missing' })
    expect(await screen.findByRole('heading', { name: 'Engineering' })).toBeInTheDocument()
    expect(screen.queryByText("Couldn't load this page.")).toBeNull()
  })

  it('falls back to the browser when the default page is withheld from this caller, not to the protected screen', async () => {
    // A space whose default page is above this caller's clearance behaves like
    // a space with no default page — never a dead end where the space used to
    // be. The tree beneath the browser still shows the page as protected.
    renderHome({ homepageId: 'page-1', access: 'denied' })
    expect(await screen.findByRole('heading', { name: 'Engineering' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Protected page' })).toBeNull()
  })
})
