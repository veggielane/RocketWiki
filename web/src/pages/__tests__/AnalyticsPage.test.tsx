import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { AnalyticsPage } from '../AnalyticsPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

const report = {
  scope: { spaceKey: 'ENG', fromUtc: '2026-08-01T00:00:00Z', toUtc: '2026-08-31T00:00:00Z', visiblePageCount: 12 },
  activity: [
    { day: '2026-08-01', views: 4, edits: 1 },
    { day: '2026-08-02', views: 0, edits: 0 },
    { day: '2026-08-03', views: 9, edits: 2 },
  ],
  mostViewed: [{ pageId: 'p1', title: 'Runbook', slug: 'runbook', spaceKey: 'ENG', count: 13 }],
  mostEdited: [{ pageId: 'p2', title: 'Handbook', slug: 'handbook', spaceKey: 'ENG', count: 3 }],
  topReaders: [{ userId: 'u1', displayName: 'Ada Lovelace', count: 13 }],
  topContributors: [{ userId: 'u2', displayName: 'Grace Hopper', count: 3 }],
  health: {
    stalePageCount: 2,
    orphanPageCount: 1,
    unlabelledPageCount: 5,
    neverViewedPageCount: 7,
    stalestPages: [{ pageId: 'p3', title: 'Old notes', slug: 'old-notes', spaceKey: 'ENG', count: 400 }],
  },
  topSearches: [{ query: 'onboarding', runCount: 6, zeroResultCount: 6 }],
  zeroResultSearches: [{ query: 'onboarding', runCount: 6, zeroResultCount: 6 }],
}

function renderPage({ analytics = report as unknown } = {}) {
  const mock = createMockUrqlClient((name) => (name === 'Analytics' ? { analytics } : undefined))
  render(
    <MemoryRouter initialEntries={['/spaces/ENG/-/analytics']}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route path="/spaces/:spaceKey/-/analytics" element={<AnalyticsPage />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

describe('AnalyticsPage', () => {
  it('states what the numbers were counted over', async () => {
    // A report filtered by access that presented itself as the whole space
    // would be quietly misleading — two admins legitimately see different totals.
    renderPage()
    expect(await screen.findByText(/Counted over the 12 pages you can view/)).toBeInTheDocument()
  })

  it('totals the period from the activity series', async () => {
    renderPage()
    const tile = await screen.findByRole('figure', { name: 'Views' })
    expect(within(tile).getByText('13')).toBeInTheDocument()
  })

  it('links ranked pages by their readable address', async () => {
    renderPage()
    expect(await screen.findByRole('link', { name: 'Runbook' })).toHaveAttribute('href', '/spaces/ENG/runbook')
  })

  it('names readers and contributors', async () => {
    // The instance owner chose attributable reading; this pins that the screen
    // actually shows it, so the choice is visible rather than implicit.
    renderPage()
    expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument()
    expect(screen.getByText('Grace Hopper')).toBeInTheDocument()
  })

  it('separates searches that found nothing from searches generally', async () => {
    renderPage()
    expect(await screen.findByRole('heading', { name: 'Searches that found nothing' })).toBeInTheDocument()
  })

  it('refetches when the period changes', async () => {
    const mock = renderPage()
    await screen.findByRole('figure', { name: 'Views' })
    fireEvent.mouseDown(screen.getByLabelText('Period'))
    fireEvent.click(screen.getByRole('option', { name: 'Last 7 days' }))

    const windows = mock.operations
      .filter((op) => op.name === 'Analytics')
      .map((op) => `${op.variables['fromUtc']}`)
    expect(new Set(windows).size).toBeGreaterThan(1)
  })

  it('says who it is for rather than showing an error when the server returns nothing', async () => {
    // §6.7: not-an-admin and no-such-space are the same null. The copy explains
    // who the screen is for instead of implying something broke.
    renderPage({ analytics: null })
    expect(
      await screen.findByText(/available to its space admins and to instance admins/),
    ).toBeInTheDocument()
  })

  it('gives the chart a text alternative, not colour alone', async () => {
    // Two series identified by colour need another channel (WCAG 1.4.1): the
    // legend names both, and the SVG carries a summary for anyone who cannot see it.
    renderPage()
    const chart = await screen.findByRole('img')
    expect(chart).toHaveAccessibleName(/13 views and 3 edits/)
    // Both series are named in the legend, so identity never rests on hue alone.
    expect(screen.getAllByText('Views').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Edits').length).toBeGreaterThan(0)
  })

  it('has no axe violations with every panel populated', async () => {
    renderPage()
    await screen.findByRole('heading', { name: 'Content health' })
    await expectNoAxeViolations()
  })
})
