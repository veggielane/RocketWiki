import { describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { AppHeader } from '../AppHeader'
import { ColorModeProvider } from '../../theme/ColorModeProvider'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

// The header mounts the notification bell, which opens a SignalR connection on
// mount; jsdom has no hub to reach (design.md §8).
vi.mock('../../realtime/transports', async () => {
  const { FakeNotificationsTransport } = await import('../../realtime/FakeNotificationsTransport')
  const transport = new FakeNotificationsTransport()
  return { getDefaultNotificationsTransport: () => transport }
})

function renderHeader(isInstanceAdmin: boolean) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'CurrentUser')
      return {
        me: {
          id: 'sub-1',
          email: 'a@b.test',
          name: 'Ada',
          groups: [],
          isAuthenticated: true,
          isInstanceAdmin,
          localUserId: 'u-1',
          hasAvatar: false,
        },
      }
    if (name === 'PersistedNotifications') return { notifications: [] }
    return undefined
  })
  render(
    <ColorModeProvider>
      <MemoryRouter>
        <UrqlProvider value={mock.client}>
          <AppHeader navOpen onToggleNav={() => {}} />
        </UrqlProvider>
      </MemoryRouter>
    </ColorModeProvider>,
  )
}

/**
 * The header offered Admin to everyone, and `/admin` is instance-admin-only
 * (router.tsx), so pressing it took a reader to a 404. That contradicted this
 * app's own posture in two places one line away: the Ask button beside it
 * returns null when the assistant is absent, and the space list hides "New
 * space" from non-admins with a comment saying "hidden rather than disabled for
 * the same reason as everywhere else".
 *
 * This is not the §6.7 read-path question — a user's own role is not a secret
 * from them, and the server gates the surfaces regardless.
 */
describe('AppHeader admin affordance', () => {
  it('offers Admin to an instance admin', async () => {
    renderHeader(true)
    expect(await screen.findByRole('link', { name: 'Admin' })).toHaveAttribute('href', '/admin')
  })

  it('offers no Admin link to anyone else, rather than a link to a 404', async () => {
    renderHeader(false)
    // Help is ungated and renders in the same cluster, so waiting on it proves
    // the cluster rendered before asserting Admin's absence.
    await screen.findByRole('link', { name: 'Help' })
    await waitFor(() => expect(screen.queryByRole('link', { name: 'Admin' })).not.toBeInTheDocument())
  })

  it('has no axe violations', async () => {
    renderHeader(true)
    await screen.findByRole('link', { name: 'Admin' })
    await expectNoAxeViolations()
  })
})
