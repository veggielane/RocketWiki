import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
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

// The account menu reads the signed-in user; nothing here needs a real OIDC client.
vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    user: { profile: { name: 'Ada', email: 'a@b.test' } },
    isAuthenticated: true,
    signoutRedirect: vi.fn(),
  }),
}))

function renderHeader({
  isInstanceAdmin = false,
  localUserId = 'u-1' as string | null,
}: { isInstanceAdmin?: boolean; localUserId?: string | null } = {}) {
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
          localUserId,
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
    renderHeader({ isInstanceAdmin: true })
    expect(await screen.findByRole('link', { name: 'Admin' })).toHaveAttribute('href', '/admin')
  })

  it('offers no Admin link to anyone else, rather than a link to a 404', async () => {
    renderHeader({ isInstanceAdmin: false })
    // Help is ungated and renders in the same cluster, so waiting on it proves
    // the cluster rendered before asserting Admin's absence.
    await screen.findByRole('link', { name: 'Help' })
    await waitFor(() => expect(screen.queryByRole('link', { name: 'Admin' })).not.toBeInTheDocument())
  })

  it('has no axe violations', async () => {
    renderHeader({ isInstanceAdmin: true })
    await screen.findByRole('link', { name: 'Admin' })
    await expectNoAxeViolations()
  })
})

/**
 * The account menu behind the avatar at the navbar's end is where a person's
 * own things live — Settings, Sign out — so it is where their own profile is
 * offered. The profile route takes the LOCAL user id, which arrives with `me`;
 * until it has, there is no address to link to, and the item is absent rather
 * than pointing somewhere that would 404.
 */
describe('AppHeader account menu', () => {
  it("offers My profile, pointing at the signed-in user's own profile", async () => {
    renderHeader({ localUserId: 'user-1' })
    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    expect(await screen.findByRole('menuitem', { name: 'My profile' })).toHaveAttribute('href', '/people/user-1')
    // Beside the account's other affordances, not instead of them.
    expect(screen.getByRole('menuitem', { name: 'Settings' })).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: 'Sign out' })).toBeInTheDocument()
  })

  it('offers no My profile item until the local id is known', async () => {
    renderHeader({ localUserId: null })
    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    // Settings is unconditional, so waiting on it proves the menu rendered
    // before asserting the profile item's absence.
    await screen.findByRole('menuitem', { name: 'Settings' })
    expect(screen.queryByRole('menuitem', { name: 'My profile' })).not.toBeInTheDocument()
  })

  it('says who the menu is about', async () => {
    renderHeader({ localUserId: 'user-1' })
    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    await screen.findByRole('menuitem', { name: 'Settings' })
    expect(screen.getByText('Ada')).toBeInTheDocument()
    expect(screen.getByText('a@b.test')).toBeInTheDocument()
  })

  it('has no axe violations with the account menu open', async () => {
    renderHeader({ localUserId: 'user-1' })
    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    await screen.findByRole('menuitem', { name: 'My profile' })
    await expectNoAxeViolations()
  })
})

/**
 * The keycap in the search box advertises Ctrl/⌘+K; this is the promise being
 * kept. The shortcut is global so it works from anywhere on the page, which is
 * what a docs site's search box does.
 */
describe('AppHeader search shortcut', () => {
  it('moves focus into the search box on Ctrl+K', async () => {
    renderHeader()
    const box = await screen.findByRole('textbox', { name: 'Search the wiki' })
    expect(box).not.toHaveFocus()
    fireEvent.keyDown(window, { key: 'k', ctrlKey: true })
    expect(box).toHaveFocus()
  })

  it('leaves a plain K alone', async () => {
    renderHeader()
    const box = await screen.findByRole('textbox', { name: 'Search the wiki' })
    fireEvent.keyDown(window, { key: 'k' })
    expect(box).not.toHaveFocus()
  })
})
