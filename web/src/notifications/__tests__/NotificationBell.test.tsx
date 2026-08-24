import { describe, expect, it } from 'vitest'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { NotificationBell } from '../NotificationBell'
import { FakeNotificationsTransport } from '../../realtime/FakeNotificationsTransport'
import { createMockUrqlClient, type MockClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

// Shaped exactly like the real `notifications` query rows (flat capped
// list, String ids, nullable pageId/spaceKey/pageTitle) — these tests run
// the generated PersistedNotifications/MarkNotificationRead documents
// against a mock exchange, so a schema drift in the operation shows up
// here as a failure, not silently.
interface PersistedRow {
  id: string
  type: string
  pageId: string | null
  spaceKey: string | null
  pageTitle: string | null
  actorDisplayName: string
  createdAtUtc: string
  readAtUtc: string | null
}

const persistedRow: PersistedRow = {
  id: '101',
  type: 'mention',
  pageId: 'page-1',
  spaceKey: 'ENG',
  pageTitle: 'Runbook',
  actorDisplayName: 'Ada Lovelace',
  createdAtUtc: '2026-08-20T10:00:00Z',
  readAtUtc: null,
}

function renderBell(rows: PersistedRow[] = [persistedRow]): {
  mock: MockClient
  transport: FakeNotificationsTransport
} {
  const transport = new FakeNotificationsTransport()
  const mock = createMockUrqlClient((name) => {
    if (name === 'PersistedNotifications') return { notifications: rows }
    if (name === 'MarkNotificationRead')
      return { markNotificationRead: { notification: { id: '101', readAtUtc: '2026-08-20T11:00:00Z' }, error: null } }
    return undefined
  })

  render(
    <MemoryRouter>
      <UrqlProvider value={mock.client}>
        <NotificationBell transport={transport} />
      </UrqlProvider>
    </MemoryRouter>,
  )
  return { mock, transport }
}

describe('NotificationBell', () => {
  it('shows the persisted catch-up list from the real notifications query', async () => {
    renderBell()

    const bell = await screen.findByRole('button', { name: 'Notifications (1 unread)' })
    fireEvent.click(bell)

    expect(await screen.findByText('Ada Lovelace mentioned you on "Runbook"')).toBeInTheDocument()
  })

  it('has no axe violations with the menu open (menu portals into document.body)', async () => {
    renderBell()
    fireEvent.click(await screen.findByRole('button', { name: 'Notifications (1 unread)' }))
    await screen.findByText('Ada Lovelace mentioned you on "Runbook"')
    await expectNoAxeViolations()
  })

  it('connects the injected transport and prepends a live push, de-duplicated against persisted rows by id', async () => {
    const { transport } = renderBell()
    await waitFor(() => expect(transport.connected).toBe(true))

    act(() => {
      transport.emit({
        id: '102',
        type: 'page_watched_changed',
        pageId: 'page-2',
        spaceKey: 'ENG',
        pageTitle: 'Telemetry',
        actorDisplayName: 'Grace Hopper',
        timestampUtc: '2026-08-21T10:00:00Z',
        readAtUtc: null,
      })
    })

    fireEvent.click(await screen.findByRole('button', { name: 'Notifications (2 unread)' }))
    expect(await screen.findByText('Grace Hopper updated "Telemetry"')).toBeInTheDocument()
    expect(screen.getByText('Ada Lovelace mentioned you on "Runbook"')).toBeInTheDocument()
  })

  it('clicking an unread notification fires the markNotificationRead mutation with the String id', async () => {
    const { mock } = renderBell()

    fireEvent.click(await screen.findByRole('button', { name: 'Notifications (1 unread)' }))
    fireEvent.click(await screen.findByText('Ada Lovelace mentioned you on "Runbook"'))

    await waitFor(() => {
      const markReads = mock.operations.filter((op) => op.name === 'MarkNotificationRead')
      expect(markReads).toHaveLength(1)
      expect(markReads[0].kind).toBe('mutation')
      expect(markReads[0].variables['input']).toEqual({ notificationId: '101' })
    })

    // And the badge clears locally without waiting for a refetch.
    expect(screen.getByRole('button', { name: 'Notifications (0 unread)' })).toBeInTheDocument()
  })

  it('does not re-fire the mutation for an already-read notification', async () => {
    const { mock } = renderBell([{ ...persistedRow, readAtUtc: '2026-08-20T11:00:00Z' }])

    fireEvent.click(await screen.findByRole('button', { name: 'Notifications (0 unread)' }))
    fireEvent.click(await screen.findByText('Ada Lovelace mentioned you on "Runbook"'))

    expect(mock.operations.filter((op) => op.name === 'MarkNotificationRead')).toHaveLength(0)
  })

  it('renders a title-less notification as plain text, never a link (design.md §8: absent, not forbidden)', async () => {
    renderBell([{ ...persistedRow, pageTitle: null, pageId: null }])

    fireEvent.click(await screen.findByRole('button', { name: 'Notifications (1 unread)' }))

    const item = await screen.findByText('Ada Lovelace mentioned you on a page you can no longer view')
    expect(item.closest('a')).toBeNull()
  })
})
