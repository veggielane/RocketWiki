import { describe, expect, it, vi } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { SideMenu } from '../SideMenu'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

// The tree's empty-state copy asks whether the viewer is an instance admin;
// nothing here needs a real OIDC client.
vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    user: { profile: { name: 'Viewer', email: 'viewer@example.test' } },
    isAuthenticated: true,
    signoutRedirect: vi.fn(),
  }),
}))

function renderSidebar(path = '/') {
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
          localUserId: 'user-1',
          hasAvatar: false,
        },
      }
    return undefined
  })
  render(
    <MemoryRouter initialEntries={[path]}>
      <UrqlProvider value={mock.client}>
        <SideMenu open />
      </UrqlProvider>
    </MemoryRouter>,
  )
}

/**
 * The sidebar is navigation and nothing else now: the brand and the signed-in
 * user moved to the navbar (AppHeader.tsx), and what is left is the grouped
 * menu — the whole-instance views first, under one landmark, with the current
 * destination marked.
 */
describe('SideMenu navigation', () => {
  it('groups the whole-instance views under the Main landmark', async () => {
    renderSidebar('/')
    const main = await screen.findByRole('navigation', { name: 'Main' })
    expect(within(main).getByRole('link', { name: 'Home' })).toHaveAttribute('href', '/')
    expect(within(main).getByRole('link', { name: 'All spaces' })).toHaveAttribute('href', '/spaces')
    expect(within(main).getByRole('link', { name: 'Graph' })).toHaveAttribute('href', '/graph')
  })

  it('marks the current destination and no other', async () => {
    renderSidebar('/graph')
    const main = await screen.findByRole('navigation', { name: 'Main' })
    expect(within(main).getByRole('link', { name: 'Graph' })).toHaveClass('Mui-selected')
    expect(within(main).getByRole('link', { name: 'Home' })).not.toHaveClass('Mui-selected')
    expect(within(main).getByRole('link', { name: 'All spaces' })).not.toHaveClass('Mui-selected')
  })

  it('carries no account controls — those are the navbar\'s', async () => {
    renderSidebar('/')
    await screen.findByRole('navigation', { name: 'Main' })
    expect(screen.queryByRole('button', { name: 'Account menu' })).not.toBeInTheDocument()
  })

  it('has no axe violations', async () => {
    renderSidebar('/spaces')
    await screen.findByRole('navigation', { name: 'Main' })
    await expectNoAxeViolations()
  })
})
