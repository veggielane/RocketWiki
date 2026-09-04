import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { Client, Provider as UrqlProvider, type Exchange, type Operation, CombinedError } from 'urql'
import { filter, map, pipe } from 'wonka'
import { AdminUsersPage } from '../AdminUsersPage'
import { AdminPage } from '../AdminPage'
import { MAX_PAGE_SIZE } from '../adminUsersPaging'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * The account roster.
 *
 * Two properties carry the weight. It shows identity and activity and nothing
 * that looks like permissions — an admin who reads a nationality column here
 * would be reading a stale copy of a decision made per request from a token.
 * And it never presents a fetched slice as the whole roster: the columns do not
 * sort, for the same reason the audit log's do not.
 */

function createFailingUrqlClient(message: string): Client {
  const failing: Exchange = () => (ops$) =>
    pipe(
      ops$,
      filter((op: Operation) => op.kind !== 'teardown'),
      map((op: Operation) => ({
        operation: op,
        data: undefined,
        error: new CombinedError({ graphQLErrors: [message] }),
        stale: false,
        hasNext: false,
      })),
    )
  return new Client({ url: '/graphql', exchanges: [failing] })
}

const account = (n: number) => ({
  id: `user-${n}`,
  displayName: `User ${String(n).padStart(2, '0')}`,
  email: `user${n}@example.internal`,
  isExternal: false,
  hasAvatar: false,
  createdAtUtc: '2026-01-02T09:00:00Z',
  lastSeenAtUtc: '2026-08-30T14:30:00Z',
})

function Where() {
  const { search } = useLocation()
  return <span data-testid="search-params">{search}</span>
}

function renderUsers({
  count = 3,
  totalCount = count as number,
  hasNextPage = false,
  entry = '/admin/users',
  overrides = [] as Partial<ReturnType<typeof account>>[],
}: {
  count?: number
  totalCount?: number
  hasNextPage?: boolean
  entry?: string
  overrides?: Partial<ReturnType<typeof account>>[]
} = {}) {
  const mock = createMockUrqlClient((name, op) => {
    if (name !== 'AdminUsers') return undefined
    const first = Number(op.variables?.first ?? 25)
    const nodes = Array.from({ length: Math.min(first, count) }, (_, i) => ({ ...account(i), ...overrides[i] }))
    return { users: { totalCount, pageInfo: { hasNextPage: hasNextPage || nodes.length < totalCount }, nodes } }
  })
  render(
    <MemoryRouter initialEntries={[entry]}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route
            path="/admin/users"
            element={
              <>
                <AdminUsersPage />
                <Where />
              </>
            }
          />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

const lastFirst = (mock: { operations: { name: string; variables: Record<string, unknown> }[] }) =>
  [...mock.operations].reverse().find((o) => o.name === 'AdminUsers')?.variables.first

describe('AdminUsersPage — what it shows', () => {
  it('lists accounts with their name, email and activity', async () => {
    renderUsers({ count: 2 })

    expect(await screen.findByText('User 00')).toBeInTheDocument()
    expect(screen.getByText('user0@example.internal')).toBeInTheDocument()
    // The viewer's own locale, via the shared formatter — the convention every
    // admin table but the audit log follows.
    expect(screen.getAllByText(new Date('2026-08-30T14:30:00Z').toLocaleString()).length).toBeGreaterThan(0)
  })

  it('badges an external account and leaves an ordinary one unlabelled', async () => {
    // A label on every row carries no signal and leaves the one that matters no
    // louder than the rest.
    renderUsers({ count: 2, overrides: [{ isExternal: true }] })
    await screen.findByText('User 00')

    expect(screen.getAllByText('External')).toHaveLength(1)
    expect(screen.queryByText('Internal')).not.toBeInTheDocument()
  })

  it('shows no nationality or group column', async () => {
    // There is no such field to select: they are token claims evaluated per
    // request, never stored per account. A column here would be a second, stale
    // answer to "who is allowed what".
    renderUsers({ count: 1 })
    await screen.findByText('User 00')

    const headers = screen.getAllByRole('columnheader').map((h) => h.textContent)
    expect(headers).toEqual(['User', 'Email', 'Account', 'Last seen', 'First seen'])
  })

  it('says where permissions actually live, for the admin who came looking', async () => {
    renderUsers({ count: 1 })
    expect(await screen.findByText(/never stored against an account/)).toBeInTheDocument()
    expect(screen.getByText(/Grants screen/)).toBeInTheDocument()
  })
})

describe('AdminUsersPage — never presents a slice as the roster', () => {
  it('makes no column sortable', async () => {
    // Same reason as the audit log: a header click would reorder the rows
    // fetched so far and present the result as an answer about everyone.
    renderUsers({ count: 3 })
    await screen.findByText('User 00')

    for (const header of screen.getAllByRole('columnheader')) {
      expect(header.className).not.toContain('columnHeader--sortable')
    }
  })

  it('states the exact total and how much of it is on screen', async () => {
    // Exact, unlike search's saturating count: nothing here is filtered per
    // row, so counting it reveals nothing a row would not.
    renderUsers({ count: 40, totalCount: 40 })
    expect(await screen.findByText(/40 accounts — showing 25/)).toBeInTheDocument()
  })
})

describe('AdminUsersPage — how much is on screen lives in the URL', () => {
  it('asks for one page by default', () => {
    expect(lastFirst(renderUsers({ count: 40 }))).toBe(25)
  })

  it('records "show more" in the address and asks for the longer run', async () => {
    const mock = renderUsers({ count: 40 })
    await screen.findByText('User 00')

    fireEvent.click(screen.getByRole('button', { name: 'Show 25 more' }))

    await waitFor(() => expect(screen.getByTestId('search-params').textContent).toContain('show=50'))
    await waitFor(() => expect(lastFirst(mock)).toBe(50))
  })

  it('rebuilds a shared link in one request', () => {
    const mock = renderUsers({ count: 40, entry: '/admin/users?show=50' })
    expect([...new Set(mock.operations.filter((o) => o.name === 'AdminUsers').map((o) => o.variables.first))]).toEqual([50])
  })

  it('never asks for more than one request may carry', () => {
    // Unlike search, where the server clamps a too-large `first` and answers
    // anyway, exceeding this connection's page size is an ERROR — so the clamp
    // has to be on this side.
    expect(lastFirst(renderUsers({ count: 40, entry: '/admin/users?show=5000' }))).toBe(MAX_PAGE_SIZE)
    expect(lastFirst(renderUsers({ count: 40, entry: '/admin/users?show=banana' }))).toBe(25)
  })

  it('stops offering more at the cap, and says why rather than going quiet', async () => {
    renderUsers({ count: 200, totalCount: 200, entry: `/admin/users?show=${MAX_PAGE_SIZE}` })
    await screen.findByText('User 00')

    expect(screen.queryByRole('button', { name: /Show \d+ more/ })).not.toBeInTheDocument()
    expect(screen.getByText(/as far as the list goes for now/)).toBeInTheDocument()
  })
})

describe('AdminUsersPage — a failed read is not an empty roster', () => {
  it('explains the refusal and renders no table', async () => {
    render(
      <MemoryRouter initialEntries={['/admin/users']}>
        <UrqlProvider value={createFailingUrqlClient('Instance admin required to view the user list.')}>
          <AdminUsersPage />
        </UrqlProvider>
      </MemoryRouter>,
    )

    expect(await screen.findByText(/available to instance admins/)).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1, name: 'Users' })).toBeInTheDocument()
    expect(screen.queryByRole('grid')).not.toBeInTheDocument()
    // "No accounts yet" on an admin roster would be alarming as well as wrong.
    expect(screen.queryByText(/No accounts yet/)).not.toBeInTheDocument()
    expect(screen.queryByText(/accounts$/)).not.toBeInTheDocument()
  })

  it('explains a genuinely empty instance differently', async () => {
    renderUsers({ count: 0, totalCount: 0 })
    expect(await screen.findByText(/No accounts yet/)).toBeInTheDocument()
  })

  it('has no axe violations with rows rendered', async () => {
    renderUsers({ count: 3 })
    await screen.findByText('User 00')
    await expectNoAxeViolations()
  })
})

describe('the admin index offers the screen', () => {
  it('links Users from the admin page', () => {
    render(
      <MemoryRouter>
        <AdminPage />
      </MemoryRouter>,
    )

    expect(screen.getByRole('link', { name: /Users/ })).toHaveAttribute('href', '/admin/users')
  })
})
