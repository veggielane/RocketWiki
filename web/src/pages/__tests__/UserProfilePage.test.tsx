import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Client, Provider as UrqlProvider, type Exchange, type Operation, CombinedError } from 'urql'
import { filter, map, pipe } from 'wonka'
import { UserProfilePage } from '../UserProfilePage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * A person's profile: the group memberships recorded at their last sign-in
 * (design.md §6.2), and nothing that reads like a permission.
 *
 * Three properties carry the weight. The groups render in the SERVER'S order
 * and in words a reader can copy into a rule. An empty list says when it was
 * empty, so it is not taken for "in no groups" in general. And no sentence
 * anywhere mentions a clearance or an eligibility — this deployment compares
 * no level against a person and gates selectors by grant alone, so a profile
 * that hinted at either would be describing a check the wiki does not make.
 * A null answer (no such user) and a failed read remain different facts that
 * take different screens.
 */

const PROFILE = {
  id: 'u-1',
  displayName: 'Ada Lovelace',
  hasAvatar: false,
  isExternal: false,
  // Ordinal order as the server sends it — deliberately not what a
  // case-insensitive sort would give, so the order assertion cannot pass by
  // re-sorting client-side.
  groups: ['Propulsion-leads', 'avionics', 'export-cleared'],
}

/** Every operation fails at the transport, so the read never arrives. */
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

function renderProfile(profile: Record<string, unknown> | null = PROFILE, client?: Client) {
  const mock = createMockUrqlClient((name) => (name === 'UserProfile' ? { userProfile: profile } : undefined))
  const view = render(
    <MemoryRouter initialEntries={['/people/u-1']}>
      <UrqlProvider value={client ?? mock.client}>
        <Routes>
          <Route path="/people/:userId" element={<UserProfilePage />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return { mock, container: view.container }
}

describe('UserProfilePage', () => {
  it('asks for the profile by the id in the route', async () => {
    const { mock } = renderProfile()
    await screen.findByRole('heading', { level: 1, name: 'Ada Lovelace' })
    expect(mock.operations.find((o) => o.name === 'UserProfile')?.variables).toEqual({ id: 'u-1' })
  })

  it("names the person as the heading and lists their groups in the server's order", async () => {
    renderProfile()
    expect(await screen.findByRole('heading', { level: 1, name: 'Ada Lovelace' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 2, name: 'Groups' })).toBeInTheDocument()
    // The list is named by the heading, so a screen reader landing on it hears
    // what these are and how many there are.
    const list = screen.getByRole('list', { name: 'Groups' })
    expect(within(list).getAllByRole('listitem').map((item) => item.textContent)).toEqual([
      'Propulsion-leads',
      'avionics',
      'export-cleared',
    ])
  })

  it('says what the list answers and what it does not — membership, never whether they may read a page', async () => {
    renderProfile()
    expect(await screen.findByText(/access rules and space grants are written against/)).toBeInTheDocument()
    expect(screen.getByText(/decided per space and per page, and is not shown here/)).toBeInTheDocument()
  })

  it('says when no group membership was recorded, rather than rendering an empty list', async () => {
    renderProfile({ ...PROFILE, groups: [] })
    expect(await screen.findByText("No group memberships were recorded at this user's last sign-in.")).toBeInTheDocument()
    expect(screen.queryByRole('list')).not.toBeInTheDocument()
    // The heading and the caption still stand: this is a recorded profile
    // with nothing in it, not a missing one.
    expect(screen.getByRole('heading', { level: 2, name: 'Groups' })).toBeInTheDocument()
  })

  it('never says clearance, eligibility or "not recorded" anywhere — it answers membership, not permission', async () => {
    const { container } = renderProfile()
    await screen.findByRole('heading', { level: 1, name: 'Ada Lovelace' })
    expect(container.textContent).not.toMatch(/clearance|eligib|not recorded|classification/i)
    // No level badge and no eligibility table: nothing on the page is styled
    // like a marking, because nothing on it is one.
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryByText(/^Clearance:/)).not.toBeInTheDocument()
  })

  it('explains an account created by sync, and shows no groups section for it', async () => {
    renderProfile({ ...PROFILE, isExternal: true })
    expect(await screen.findByRole('heading', { level: 1, name: 'Ada Lovelace' })).toBeInTheDocument()
    expect(screen.getByText(/created by sync and has never signed in here/)).toBeInTheDocument()
    expect(screen.getByText(/no group membership has been recorded for it/)).toBeInTheDocument()
    expect(screen.queryByRole('heading', { level: 2, name: 'Groups' })).not.toBeInTheDocument()
    expect(screen.queryByRole('list')).not.toBeInTheDocument()
    // Whatever the wire carried for a shadow account is not shown either.
    expect(screen.queryByText('avionics')).not.toBeInTheDocument()
  })

  it('says everything is as of the last sign-in and managed in Keycloak, with no timestamp', async () => {
    renderProfile()
    expect(await screen.findByText(/Recorded at this user's last sign-in/)).toBeInTheDocument()
    expect(screen.getByText(/Group membership is managed in Keycloak, not in RocketWiki/)).toBeInTheDocument()
  })

  it('shows none of what the admin roster keeps to itself', async () => {
    // Nationality, email and last-seen stay admin-only (design.md §6.2) — the
    // profile must not grow them back by accident.
    const { container } = renderProfile()
    await screen.findByRole('heading', { level: 1, name: 'Ada Lovelace' })
    expect(container.textContent).not.toMatch(/nationality|email|last seen/i)
  })

  it('renders the not-found screen for an id that matches nobody', async () => {
    // Null is the server's answer for an unknown id — the same treatment an
    // unknown page gets.
    renderProfile(null)
    expect(await screen.findByRole('heading', { level: 1, name: '404' })).toBeInTheDocument()
  })

  it('says the read failed, not that the user is gone, when the request never arrived', async () => {
    renderProfile(PROFILE, failingClient())
    expect(await screen.findByText("Couldn't load this profile.")).toBeInTheDocument()
    expect(screen.queryByText('404')).not.toBeInTheDocument()
  })

  it('has no axe violations', async () => {
    renderProfile()
    await screen.findByRole('list', { name: 'Groups' })
    await expectNoAxeViolations()
  })

  it('has no axe violations in the empty and external states', async () => {
    renderProfile({ ...PROFILE, groups: [] })
    await screen.findByText(/No group memberships were recorded/)
    await expectNoAxeViolations()
  })

  it('has no axe violations for an account created by sync', async () => {
    renderProfile({ ...PROFILE, isExternal: true })
    await screen.findByText(/created by sync/)
    await expectNoAxeViolations()
  })
})
