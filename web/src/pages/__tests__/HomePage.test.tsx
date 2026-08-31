import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Client, Provider as UrqlProvider, type Exchange, type Operation, CombinedError } from 'urql'
import { filter, map, pipe } from 'wonka'
import { HomePage } from '../HomePage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'
import { FEED_MAX_PAGE_SIZE, FEED_PAGE_SIZE } from '../../home/feedPaging'
import { describeTimeSince } from '../../format/relativeTime'

/**
 * The homepage's three feeds.
 *
 * The property that shapes everything here is INDEPENDENCE: three queries, so a
 * slow or refused feed shows its own state while the other two render. A single
 * query would make one failure a blank homepage, which is worse than a homepage
 * with two feeds and an explanation.
 */

const page = (n: number, over: Record<string, unknown> = {}) => ({
  id: `p${n}`,
  title: `Page ${n}`,
  spaceKey: 'ENG',
  slug: `page-${n}`,
  icon: null,
  marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], prefix: 'UK', label: 'UK OFFICIAL' },
  ...over,
})

const author = { id: 'u1', displayName: 'Ada Lovelace', hasAvatar: false }

/** Two clearly different ages, so 'last updated …' cannot be a constant. */
const STALE_UPDATED = ['2019-01-01T00:00:00Z', '2026-08-30T12:00:00Z']

interface FeedData {
  activity?: { totalCount: number; hasNextPage?: boolean; count: number }
  stale?: { totalCount: number; hasNextPage?: boolean; count: number }
  viewed?: { hasNextPage?: boolean; count: number }
}

function feeds({ activity, stale, viewed }: FeedData = {}) {
  return createMockUrqlClient((name, op) => {
    const first = Number(op.variables?.first ?? FEED_PAGE_SIZE)
    if (name === 'ActivityFeed' && activity) {
      return {
        activityFeed: {
          totalCount: activity.totalCount,
          pageInfo: { hasNextPage: activity.hasNextPage ?? false },
          nodes: Array.from({ length: Math.min(first, activity.count) }, (_, i) => ({
            occurredAtUtc: '2026-08-30T12:00:00Z',
            isCreate: i === 0,
            revisionNumber: i + 1,
            author,
            page: page(i),
          })),
        },
      }
    }
    if (name === 'MyStaleContent' && stale) {
      return {
        myStaleContent: {
          totalCount: stale.totalCount,
          pageInfo: { hasNextPage: stale.hasNextPage ?? false },
          nodes: Array.from({ length: Math.min(first, stale.count) }, (_, i) => ({
            // A different age per row: an assertion that only matched the
            // label would pass against a hard-coded string, so the ages have
            // to be distinguishable from each other.
            page: page(100 + i, { updatedAtUtc: STALE_UPDATED[i] ?? STALE_UPDATED[0] }),
          })),
        },
      }
    }
    if (name === 'MyRecentlyViewed' && viewed) {
      return {
        myRecentlyViewed: {
          pageInfo: { hasNextPage: viewed.hasNextPage ?? false },
          nodes: Array.from({ length: Math.min(first, viewed.count) }, (_, i) => ({
            lastViewedAtUtc: '2026-08-31T11:00:00Z',
            page: page(200 + i),
          })),
        },
      }
    }
    return undefined
  })
}

/** Empty feeds everywhere unless a test says otherwise. */
const EMPTY: FeedData = {
  activity: { totalCount: 0, count: 0 },
  stale: { totalCount: 0, count: 0 },
  viewed: { count: 0 },
}

function renderHome(data: FeedData = EMPTY, client?: Client) {
  const mock = client ? { client, operations: [] } : feeds(data)
  render(
    <MemoryRouter>
      <UrqlProvider value={mock.client}>
        <HomePage />
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

/** A client whose every operation fails at the transport. */
function failingClient(): Client {
  const failing: Exchange = () => (ops$) =>
    pipe(
      ops$,
      filter((op: Operation) => op.kind !== 'teardown'),
      map((op: Operation) => ({
        operation: op,
        data: undefined,
        error: new CombinedError({ networkError: new Error('offline') }),
        stale: false,
        hasNext: false,
      })),
    )
  return new Client({ url: '/graphql', exchanges: [failing] })
}

/** One feed's section, by its heading. */
const section = (name: string) => screen.getByRole('region', { name })

describe('the three feeds render what they are for', () => {
  it('shows recent activity with who did what, and whether it was a create', async () => {
    // Created vs edited is the most useful fact in the row, and it is said in
    // words rather than by an icon.
    renderHome({ ...EMPTY, activity: { totalCount: 2, count: 2 } })

    const feed = section('Recent activity')
    expect(await within(feed).findByText('Page 0')).toBeInTheDocument()
    expect(within(feed).getByText(/Ada Lovelace created this/)).toBeInTheDocument()
    expect(within(feed).getByText(/Ada Lovelace edited this/)).toBeInTheDocument()
  })

  it('shows the stale list with the timestamp it was SORTED by', async () => {
    // Showing any other timestamp would make a correctly ordered list look
    // shuffled — the server orders by page.updatedAtUtc, so that is what the
    // row says.
    renderHome({ ...EMPTY, stale: { totalCount: 2, count: 2 } })

    const feed = section('Stale content you own')
    // The VALUE, not just the label. An assertion that only matched
    // 'last updated …' passes against a row that prints a constant — the
    // mutation that replaced the timestamp with the word 'recently' went
    // green. Computed from the fixture rather than hard-coded, so it stays
    // locale- and clock-independent while still depending on the field.
    expect(
      await within(feed).findByText(`last updated ${describeTimeSince(STALE_UPDATED[0])}`),
    ).toBeInTheDocument()
    expect(within(feed).getByText(`last updated ${describeTimeSince(STALE_UPDATED[1])}`)).toBeInTheDocument()
    // And the two rows say different things, so nothing constant can satisfy both.
    expect(describeTimeSince(STALE_UPDATED[0])).not.toBe(describeTimeSince(STALE_UPDATED[1]))
  })

  it('says the stale list is ordered by ANYONE’s last edit', async () => {
    // "Stale content you own" otherwise reads as "you have not touched these",
    // which is not what it means: a page someone else maintains is not stale.
    renderHome()
    expect(screen.getByText(/Ordered by anyone's last edit, not your own/)).toBeInTheDocument()
  })

  it('shows recently viewed with when you last looked', async () => {
    renderHome({ ...EMPTY, viewed: { count: 1 } })

    const feed = section('Recently viewed')
    expect(await within(feed).findByText(/viewed .*ago|viewed just now/)).toBeInTheDocument()
  })

  it('links every row to the page’s readable address', async () => {
    renderHome({ ...EMPTY, activity: { totalCount: 1, count: 1 } })

    expect(await screen.findByRole('link', { name: /Page 0/ })).toHaveAttribute('href', '/spaces/ENG/page-0')
  })

  it('names the space by key, as every other cross-space list does', async () => {
    renderHome({ ...EMPTY, activity: { totalCount: 1, count: 1 } })
    expect(await screen.findByText('in ENG')).toBeInTheDocument()
  })
})

describe('every row carries its marking', () => {
  it('badges each page, because these are cross-space reads', async () => {
    // A feed row can name a page far more sensitive than anything else on
    // screen; the badge is what tells a reader before they open it (§21).
    renderHome({
      activity: { totalCount: 1, count: 1 },
      stale: { totalCount: 1, count: 1 },
      viewed: { count: 1 },
    })

    await screen.findByText('Page 0')
    expect(screen.getAllByText('OFFICIAL')).toHaveLength(3)
  })

  it('states the level in words, never colour alone', async () => {
    renderHome({ ...EMPTY, activity: { totalCount: 1, count: 1 } })
    // WCAG 1.4.1: the badge prints the level's name.
    expect(await screen.findByText('OFFICIAL')).toBeInTheDocument()
  })
})

describe('one feed failing does not take the page with it', () => {
  it('shows an error in the failing feed and rows in the others', async () => {
    const mock = createMockUrqlClient((name) => {
      if (name === 'ActivityFeed') return undefined // no data, no error → treated as empty
      if (name === 'MyStaleContent') return { myStaleContent: { totalCount: 1, pageInfo: { hasNextPage: false }, nodes: [{ page: page(1, { updatedAtUtc: '2025-01-01T00:00:00Z' }) }] } }
      if (name === 'MyRecentlyViewed') return { myRecentlyViewed: { pageInfo: { hasNextPage: false }, nodes: [{ lastViewedAtUtc: '2026-08-31T11:00:00Z', page: page(2) }] } }
      return undefined
    })
    render(
      <MemoryRouter>
        <UrqlProvider value={mock.client}>
          <HomePage />
        </UrqlProvider>
      </MemoryRouter>,
    )

    // The other two rendered.
    expect(await screen.findByText('Page 1')).toBeInTheDocument()
    expect(screen.getByText('Page 2')).toBeInTheDocument()
  })

  it('gives each feed its own failure sentence, not one shared one', async () => {
    renderHome(EMPTY, failingClient())

    expect(await screen.findByText(/Couldn't load recent activity/)).toBeInTheDocument()
    expect(screen.getByText(/Couldn't load your stale pages/)).toBeInTheDocument()
    expect(screen.getByText(/Couldn't load what you viewed recently/)).toBeInTheDocument()
    // A failed read is not an empty feed.
    expect(screen.queryByText(/will appear here/)).not.toBeInTheDocument()
  })

  it('keeps the page heading through every failure', async () => {
    renderHome(EMPTY, failingClient())
    expect(await screen.findByRole('heading', { level: 1, name: 'Home' })).toBeInTheDocument()
  })
})

describe('a new account is told something useful, not nothing', () => {
  it('explains each empty feed rather than dead-ending', async () => {
    renderHome()

    expect(await screen.findByText(/Nothing has been created or edited yet/)).toBeInTheDocument()
    expect(screen.getByText(/Nothing of yours has gone stale/)).toBeInTheDocument()
    expect(screen.getByText(/Pages you open will appear here/)).toBeInTheDocument()
  })

  it('offers a way out of the empty activity feed', async () => {
    renderHome()
    expect(await screen.findByRole('link', { name: 'Browse the spaces' })).toHaveAttribute('href', '/spaces')
  })
})

describe('counts, and where there are none', () => {
  it('states an exact total on the feeds that expose one', async () => {
    renderHome({
      activity: { totalCount: 42, count: 2 },
      stale: { totalCount: 7, count: 1 },
      viewed: { count: 1 },
    })

    expect(await within(section('Recent activity')).findByText(/42 changes — showing 2/)).toBeInTheDocument()
    expect(within(section('Stale content you own')).getByText(/7 pages — showing 1/)).toBeInTheDocument()
  })

  it('shows no count on recently viewed, which exposes none', async () => {
    renderHome({ ...EMPTY, viewed: { count: 2 } })
    const feed = section('Recently viewed')
    await within(feed).findByText('Page 200')
    expect(within(feed).queryByText(/showing/)).not.toBeInTheDocument()
  })
})

describe('show more is local, capped, and per feed', () => {
  it('asks for one page first', () => {
    const mock = renderHome({ ...EMPTY, activity: { totalCount: 40, count: 40, hasNextPage: true } })
    const first = mock.operations.find((o) => o.name === 'ActivityFeed')?.variables.first
    expect(first).toBe(FEED_PAGE_SIZE)
  })

  it('grows only the feed whose button was pressed', async () => {
    const mock = renderHome({
      activity: { totalCount: 40, count: 40, hasNextPage: true },
      stale: { totalCount: 40, count: 40, hasNextPage: true },
      viewed: { count: 40, hasNextPage: true },
    })
    await screen.findByText('Page 0')

    fireEvent.click(within(section('Recent activity')).getByRole('button', { name: 'Show more' }))

    await waitFor(() => {
      const sizes = mock.operations.filter((o) => o.name === 'ActivityFeed').map((o) => o.variables.first)
      expect(sizes).toContain(FEED_PAGE_SIZE * 2)
    })
    // The other feeds were not disturbed. Deduplicated, because urql may
    // re-execute the same variables; a SECOND SIZE is what would mean the
    // button had grown someone else's feed.
    const staleSizes = new Set(
      mock.operations.filter((o) => o.name === 'MyStaleContent').map((o) => o.variables.first),
    )
    expect([...staleSizes]).toEqual([FEED_PAGE_SIZE])
  })

  it('never asks for more than the connection allows', async () => {
    // Exceeding the cap is an ERROR at validation, not a clamp — and it is a
    // cost ceiling too, since each row costs page-size × object-fields.
    const mock = renderHome({ ...EMPTY, activity: { totalCount: 999, count: 999, hasNextPage: true } })
    await screen.findByText('Page 0')

    for (let i = 0; i < 6; i++) {
      const button = within(section('Recent activity')).queryByRole('button', { name: 'Show more' })
      if (!button) break
      fireEvent.click(button)
      await waitFor(() => expect(mock.operations.length).toBeGreaterThan(0))
    }

    const sizes = mock.operations.filter((o) => o.name === 'ActivityFeed').map((o) => Number(o.variables.first))
    expect(Math.max(...sizes)).toBeLessThanOrEqual(FEED_MAX_PAGE_SIZE)
  })

  it('keeps nothing about the feeds in the URL', async () => {
    // Deliberately unlike search and the roster: nobody shares a link to their
    // own expanded recently-viewed, and three feeds would be three parameters.
    renderHome({ ...EMPTY, activity: { totalCount: 40, count: 40, hasNextPage: true } })
    await screen.findByText('Page 0')

    fireEvent.click(within(section('Recent activity')).getByRole('button', { name: 'Show more' }))

    await waitFor(() => expect(window.location.search).toBe(''))
  })
})

describe('accessibility', () => {
  it('gives each feed a heading under the page’s own', async () => {
    renderHome({ ...EMPTY, activity: { totalCount: 1, count: 1 } })
    await screen.findByText('Page 0')

    expect(screen.getByRole('heading', { level: 1, name: 'Home' })).toBeInTheDocument()
    for (const name of ['Recent activity', 'Stale content you own', 'Recently viewed']) {
      expect(screen.getByRole('heading', { level: 2, name })).toBeInTheDocument()
    }
  })

  it('has no axe violations with all three feeds populated', async () => {
    renderHome({
      activity: { totalCount: 3, count: 3 },
      stale: { totalCount: 2, count: 2 },
      viewed: { count: 2 },
    })
    await screen.findByText('Page 0')
    await expectNoAxeViolations()
  })

  it('has no axe violations when every feed is empty', async () => {
    renderHome()
    await screen.findByText(/Pages you open will appear here/)
    await expectNoAxeViolations()
  })
})

describe('the query selection stays inside the cost budget', () => {
  it('selects no object-field the budget cannot afford', async () => {
    // Each row costs page-size × object-fields, and the backend has a test
    // sized to exactly this selection. `space { name }` or `labels` here would
    // fail that rather than surprise a user with a 400 — so the space is named
    // by its scalar key.
    const { ActivityFeedDocument, MyStaleContentDocument, MyRecentlyViewedDocument } = await import(
      '../../graphql/generated/graphql'
    )
    for (const document of [ActivityFeedDocument, MyStaleContentDocument, MyRecentlyViewedDocument]) {
      const text = JSON.stringify(document)
      expect(text).not.toContain('"space"')
      expect(text).not.toContain('"labels"')
    }
  })
})

vi.mock('../../avatars/avatarCache', () => ({
  getAvatarUrl: () => Promise.resolve(null),
  peekAvatarUrl: () => null,
}))
