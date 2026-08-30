import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { Client, Provider as UrqlProvider, type Exchange, type Operation, CombinedError } from 'urql'
import { filter, map, pipe } from 'wonka'
import { AuditLogPage } from '../AuditLogPage'
import { SyncStatusPage } from '../SyncStatusPage'
import { AnalyticsPage } from '../AnalyticsPage'
import { TrashPage } from '../TrashPage'
import { ConfirmDialog } from '../../feedback/ConfirmDialog'
import { measureFor } from '../../app/contentMeasure'
import { createMockUrqlClient } from '../../test/mockUrqlClient'

/**
 * The admin screens as a SET.
 *
 * Every fix pinned here was a divergence between siblings — one screen's error
 * destroying its heading while the next kept it, one table sorting a fetched
 * slice while the identical-looking one sorted a complete array. The `it`s below
 * fail on the code as it stood before this pass.
 */

/** A client whose every operation fails at the transport, for the error branches. */
function createFailingUrqlClient(): Client {
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

function Where() {
  const { search } = useLocation()
  return <span data-testid="search-params">{search}</span>
}

const auditEvent = (id: string) => ({
  id,
  timestampUtc: '2026-08-29T09:14:22.481Z',
  userId: 'u1',
  userDisplayName: 'Ada Lovelace',
  action: 'page.view',
  subjectType: 'PAGE',
  subjectId: 'p1',
  spaceKey: 'ENG',
  outcome: 'SUCCESS',
  channel: 'web',
  mcpClient: null,
  detailsJson: null,
})

function renderAuditLog({ nodes = [auditEvent('a1')], entry = '/admin/audit' } = {}) {
  const mock = createMockUrqlClient((name) =>
    name === 'AuditEvents'
      ? {
          auditEvents: {
            totalCount: nodes.length,
            pageInfo: { hasNextPage: false, endCursor: null },
            nodes,
          },
        }
      : undefined,
  )
  render(
    <MemoryRouter initialEntries={[entry]}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route
            path="/admin/audit"
            element={
              <>
                <AuditLogPage />
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

/** The filter as the last AuditEvents operation asked for it. */
const lastFilter = (mock: { operations: { name: string; variables: Record<string, unknown> }[] }) =>
  [...mock.operations].reverse().find((o) => o.name === 'AuditEvents')?.variables.filter as
    | Record<string, unknown>
    | undefined

describe('the audit log does not offer a sort it cannot honour', () => {
  it('makes no column sortable, because the rows are a fetched slice', async () => {
    // `rows` is what has been loaded so far over a 100-at-a-time cursor
    // connection. A header click reordered that slice and presented it as
    // "oldest first" — an answer about 100 rows dressed as an answer about the
    // log.
    renderAuditLog()
    const headers = await screen.findAllByRole('columnheader')
    expect(headers.length).toBeGreaterThan(0)
    for (const header of headers) {
      // MUI marks a sortable header with the class and gives it a sort button;
      // it emits `aria-sort="none"` either way, so the class is the signal.
      expect(header.className).not.toContain('columnHeader--sortable')
      expect(header.querySelector('.MuiDataGrid-iconButtonContainer button')).toBeNull()
    }
  })

  it('offers one filter system, not the grid’s as well as its own', () => {
    // Six server-side fields sit directly above the grid; the grid's own panel
    // filters only the fetched slice, with identical-looking controls.
    renderAuditLog()
    expect(screen.queryByRole('button', { name: /^Filters?$/i })).not.toBeInTheDocument()
  })
})

describe('the audit log puts its filters in the URL', () => {
  it('commits a typed action filter to the query string and to the request', async () => {
    const mock = renderAuditLog()

    fireEvent.change(screen.getByLabelText('Action'), { target: { value: 'page.delete' } })

    await waitFor(() => expect(screen.getByTestId('search-params').textContent).toContain('action=page.delete'), {
      timeout: 3000,
    })
    await waitFor(() => expect(lastFilter(mock)?.action).toBe('page.delete'), { timeout: 3000 })
  })

  it('reads its filters back out of the address, so a filtered view can be sent to someone', () => {
    const mock = renderAuditLog({ entry: '/admin/audit?outcome=DENIED&subject=SPACE&user=u9' })

    expect(screen.getByLabelText('User ID')).toHaveValue('u9')
    expect(lastFilter(mock)).toMatchObject({ outcome: 'DENIED', subjectType: 'SPACE', userId: 'u9' })
  })

  it('debounces typing rather than firing a request per keystroke', () => {
    const mock = renderAuditLog()
    const before = mock.operations.filter((o) => o.name === 'AuditEvents').length

    const field = screen.getByLabelText('User ID')
    fireEvent.change(field, { target: { value: 'u' } })
    fireEvent.change(field, { target: { value: 'u9' } })
    fireEvent.change(field, { target: { value: 'u91' } })

    expect(mock.operations.filter((o) => o.name === 'AuditEvents').length).toBe(before)
  })

  it('clears every filter at once', async () => {
    renderAuditLog({ entry: '/admin/audit?outcome=DENIED&user=u9&from=2026-08-01' })

    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }))

    await waitFor(() => expect(screen.getByTestId('search-params').textContent).toBe(''))
    expect(screen.getByLabelText('User ID')).toHaveValue('')
  })
})

describe('the audit log says what an empty or failed read means', () => {
  it('explains an empty result differently when filters are on', async () => {
    renderAuditLog({ nodes: [], entry: '/admin/audit?outcome=DENIED' })
    expect(await screen.findByText(/No recorded events match these filters/)).toBeInTheDocument()
  })

  it('explains an unfiltered empty log as "nothing has happened yet"', async () => {
    renderAuditLog({ nodes: [] })
    expect(await screen.findByText(/No events have been recorded yet/)).toBeInTheDocument()
  })

  it('does not render an empty grid underneath its own error', async () => {
    render(
      <MemoryRouter initialEntries={['/admin/audit']}>
        <UrqlProvider value={createFailingUrqlClient()}>
          <AuditLogPage />
        </UrqlProvider>
      </MemoryRouter>,
    )

    expect(await screen.findByText(/Couldn't load audit events/)).toBeInTheDocument()
    // "No rows" plus "0 matching events" under a failure claimed a successful,
    // empty query.
    expect(screen.queryByRole('grid')).not.toBeInTheDocument()
    expect(screen.queryByText(/matching event/)).not.toBeInTheDocument()
    // The heading survives, on every branch.
    expect(screen.getByRole('heading', { level: 1, name: 'Audit log' })).toBeInTheDocument()
  })
})

describe('the admin set can be asked again', () => {
  it('refetches the audit log from the network', async () => {
    const mock = renderAuditLog()
    const before = mock.operations.length

    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))

    await waitFor(() => expect(mock.operations.length).toBeGreaterThan(before))
  })

  it('refetches sync status — the numbers people re-check', async () => {
    const mock = createMockUrqlClient((name) =>
      name === 'SyncStatus'
        ? { syncStatus: { localInstanceId: 'LOW', exportedSpaces: [], origins: [] } }
        : undefined,
    )
    render(
      <UrqlProvider value={mock.client}>
        <SyncStatusPage />
      </UrqlProvider>,
    )
    const before = mock.operations.length

    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))

    await waitFor(() => expect(mock.operations.length).toBeGreaterThan(before))
  })
})

describe('a failed read never costs a screen its heading', () => {
  it('keeps the sync-status h1 and shows the failure inline', async () => {
    render(
      <UrqlProvider value={createFailingUrqlClient()}>
        <SyncStatusPage />
      </UrqlProvider>,
    )

    expect(await screen.findByText(/Couldn't load sync status/)).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1, name: 'Sync status' })).toBeInTheDocument()
  })

  it('keeps the trash h1 while it loads and when it fails', async () => {
    render(
      <MemoryRouter initialEntries={['/spaces/ENG/-/trash']}>
        <UrqlProvider value={createFailingUrqlClient()}>
          <Routes>
            <Route path="/spaces/:spaceKey/-/trash" element={<TrashPage />} />
          </Routes>
        </UrqlProvider>
      </MemoryRouter>,
    )

    expect(await screen.findByText(/Couldn't load trash/)).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1, name: /Trash/ })).toBeInTheDocument()
    // No empty list underneath the failure — that is the shape of a successful,
    // empty read.
    expect(screen.queryByText(/Trash is empty/)).not.toBeInTheDocument()
  })

  it('tells an analytics transport failure apart from "you are not an admin"', async () => {
    render(
      <MemoryRouter initialEntries={['/admin/analytics']}>
        <UrqlProvider value={createFailingUrqlClient()}>
          <AnalyticsPage />
        </UrqlProvider>
      </MemoryRouter>,
    )

    // The old copy told an admin whose API was down that they lacked permission.
    expect(await screen.findByText(/Couldn't load the usage report/)).toBeInTheDocument()
    expect(screen.queryByText(/available to instance admins/)).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1, name: 'Site analytics' })).toBeInTheDocument()
  })

  it('still says "not yours" when the server answers with null', async () => {
    const mock = createMockUrqlClient((name) => (name === 'Analytics' ? { analytics: null } : undefined))
    render(
      <MemoryRouter initialEntries={['/admin/analytics']}>
        <UrqlProvider value={mock.client}>
          <AnalyticsPage />
        </UrqlProvider>
      </MemoryRouter>,
    )

    expect(await screen.findByText(/Site analytics are available to instance admins/)).toBeInTheDocument()
  })
})

describe('sync status says a fact once', () => {
  it('shows the empty sentence instead of an empty table, not as well as', async () => {
    const mock = createMockUrqlClient((name) =>
      name === 'SyncStatus'
        ? { syncStatus: { localInstanceId: 'LOW', exportedSpaces: [], origins: [] } }
        : undefined,
    )
    render(
      <UrqlProvider value={mock.client}>
        <SyncStatusPage />
      </UrqlProvider>,
    )

    expect(await screen.findByText(/No spaces are flagged for export/)).toBeInTheDocument()
    expect(screen.queryByRole('grid', { name: 'Exported spaces' })).not.toBeInTheDocument()
  })
})

describe('ConfirmDialog', () => {
  it('keeps the question readable through the close transition', () => {
    // Callers derive the title from the same state their handler clears, so the
    // last thing on screen was `Delete :null:?` while the paper faded.
    const { rerender } = render(
      <ConfirmDialog open title="Delete :rocket:?" confirmLabel="Delete" onCancel={vi.fn()} onConfirm={vi.fn()}>
        Existing content shows the literal text.
      </ConfirmDialog>,
    )

    rerender(
      <ConfirmDialog open={false} title="Delete :null:?" confirmLabel="Delete" onCancel={vi.fn()} onConfirm={vi.fn()}>
        Existing content shows the literal text.
      </ConfirmDialog>,
    )

    expect(screen.queryByText('Delete :null:?')).not.toBeInTheDocument()
    expect(screen.getByText('Delete :rocket:?')).toBeInTheDocument()
  })

  it('shows the confirm action as in-flight while the caller is busy', () => {
    render(
      <ConfirmDialog open busy title="Delete :rocket:?" confirmLabel="Delete" onCancel={vi.fn()} onConfirm={vi.fn()}>
        Gone.
      </ConfirmDialog>,
    )

    expect(screen.getByRole('button', { name: 'Delete' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled()
  })
})

describe('the content measure belongs to the screen, not the address', () => {
  it('gives both mounts of the analytics screen the same width', () => {
    // The identical component laid its charts out differently depending on
    // which link you followed.
    expect(measureFor('/spaces/ENG/-/analytics')).toBe(measureFor('/admin/analytics'))
  })

  it('still leaves prose screens at the reading measure', () => {
    expect(measureFor('/spaces/ENG/runbook')).toBeLessThan(measureFor('/admin/audit'))
  })
})
