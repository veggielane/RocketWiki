import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { SideMenu } from '../SideMenu'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

// The rail reads the signed-in user for its account block; nothing here needs
// a real OIDC client.
vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    user: { profile: { name: 'Viewer', email: 'viewer@example.test' } },
    isAuthenticated: true,
    signoutRedirect: vi.fn(),
  }),
}))

function renderRail(localUserId: string | null) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceList') return { spaces: [] }
    if (name === 'CurrentUser')
      return {
        me: {
          id: 'sub-1',
          email: 'viewer@example.test',
          name: 'Viewer',
          groups: [],
          isAuthenticated: true,
          isInstanceAdmin: false,
          localUserId,
          hasAvatar: false,
        },
      }
    return undefined
  })
  render(
    <MemoryRouter>
      <UrqlProvider value={mock.client}>
        <SideMenu open />
      </UrqlProvider>
    </MemoryRouter>,
  )
}

/**
 * The account menu at the bottom of the rail is where a person's own things
 * live — Settings, Sign out — so it is where their own profile is offered.
 * The profile route takes the LOCAL user id, which arrives with `me`; until
 * it has, there is no address to link to, and the item is absent rather than
 * pointing somewhere that would 404.
 */
describe('SideMenu account menu', () => {
  it("offers My profile, pointing at the signed-in user's own profile", async () => {
    renderRail('user-1')
    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    expect(await screen.findByRole('menuitem', { name: 'My profile' })).toHaveAttribute('href', '/people/user-1')
    // Beside the account's other affordances, not instead of them.
    expect(screen.getByRole('menuitem', { name: 'Settings' })).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: 'Sign out' })).toBeInTheDocument()
  })

  it('offers no My profile item until the local id is known', async () => {
    renderRail(null)
    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    // Settings is unconditional, so waiting on it proves the menu rendered
    // before asserting the profile item's absence.
    await screen.findByRole('menuitem', { name: 'Settings' })
    expect(screen.queryByRole('menuitem', { name: 'My profile' })).not.toBeInTheDocument()
  })

  it('has no axe violations with the account menu open', async () => {
    renderRail('user-1')
    fireEvent.click(await screen.findByRole('button', { name: 'Account menu' }))
    await screen.findByRole('menuitem', { name: 'My profile' })
    await expectNoAxeViolations()
  })
})
