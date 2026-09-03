import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Client, Provider as UrqlProvider, type Exchange, type Operation, CombinedError } from 'urql'
import { filter, map, pipe } from 'wonka'
import { UserProfilePage } from '../UserProfilePage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * A person's profile: clearance and per-category selector eligibility, as
 * recorded at their last sign-in (design.md §6.2).
 *
 * Three properties carry the weight. An UNRECORDED clearance says so and never
 * shows the floor the wire still carries — badging a default as a clearance
 * would tell a colleague they may share material the person was never cleared
 * for. Every eligibility answer is WORDS beside an icon, never a colour alone.
 * And a null answer (no such user) and a failed read are different facts that
 * take different screens.
 */

const PROFILE = {
  id: 'u-1',
  displayName: 'Ada Lovelace',
  hasAvatar: false,
  isExternal: false,
  clearance: 'SECRET',
  clearanceName: 'SECRET',
  clearanceRecorded: true,
  // Catalog order, deliberately not alphabetical, so the order assertion
  // cannot pass by sorting.
  selectorEligibility: [
    { category: 'FRUIT', requiresAttribute: true, eligible: true },
    { category: 'COLOUR', requiresAttribute: true, eligible: false },
    { category: 'ANIMAL', requiresAttribute: false, eligible: true },
  ],
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
  render(
    <MemoryRouter initialEntries={['/people/u-1']}>
      <UrqlProvider value={client ?? mock.client}>
        <Routes>
          <Route path="/people/:userId" element={<UserProfilePage />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

describe('UserProfilePage', () => {
  it('asks for the profile by the id in the route', async () => {
    const mock = renderProfile()
    await screen.findByRole('heading', { level: 1, name: 'Ada Lovelace' })
    expect(mock.operations.find((o) => o.name === 'UserProfile')?.variables).toEqual({ id: 'u-1' })
  })

  it('names the person as the heading and badges a recorded clearance', async () => {
    renderProfile()
    expect(await screen.findByRole('heading', { level: 1, name: 'Ada Lovelace' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 2, name: 'Clearance' })).toBeInTheDocument()
    const badge = screen.getByText('SECRET')
    // The server's spelling, verbatim, behind a lead-in only a screen reader
    // hears — and the lead-in says CLEARANCE, not classification: this is a
    // person, not a page.
    expect(badge.closest('span')?.textContent).toBe('Clearance: SECRET')
  })

  it('lists one row per category in catalog order, each answer in words', async () => {
    renderProfile()
    const table = await screen.findByRole('table', { name: 'Selector eligibility' })
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows.map((row) => within(row).getByRole('rowheader').textContent)).toEqual(['FRUIT', 'COLOUR', 'ANIMAL'])
    // Text, never colour alone (WCAG 1.4.1): each row SAYS its answer.
    expect(within(rows[0]!).getByText('Eligible')).toBeInTheDocument()
    expect(within(rows[1]!).getByText('Not eligible')).toBeInTheDocument()
    expect(within(rows[2]!).getByText('Everyone is eligible')).toBeInTheDocument()
  })

  it('says "Everyone is eligible" for a category with no claim gate, not merely "Eligible"', async () => {
    // Different facts: one is about this person's claims, the other about the
    // category. A colleague deciding whether to share needs to know which.
    renderProfile({ ...PROFILE, selectorEligibility: [{ category: 'ANIMAL', requiresAttribute: false, eligible: true }] })
    const table = await screen.findByRole('table', { name: 'Selector eligibility' })
    expect(within(table).getByText('Everyone is eligible')).toBeInTheDocument()
    expect(within(table).queryByText('Eligible')).not.toBeInTheDocument()
  })

  it('says "Not recorded" for an unrecorded clearance and never shows the floor as one they hold', async () => {
    // The wire still carries OFFICIAL-SENSITIVE — the gate's floor for an absent
    // claim. That is what the gate does, not a fact about this person.
    renderProfile({
      ...PROFILE,
      clearance: 'OFFICIAL_SENSITIVE',
      clearanceName: 'OFFICIAL-SENSITIVE',
      clearanceRecorded: false,
    })
    expect(await screen.findByText('Not recorded')).toBeInTheDocument()
    expect(screen.getByText(/No recognised clearance claim was recorded/)).toBeInTheDocument()
    expect(screen.queryByText('OFFICIAL-SENSITIVE')).not.toBeInTheDocument()
    expect(screen.queryByText(/^Clearance:/)).not.toBeInTheDocument()
  })

  it('explains an account created by sync, and shows no clearance or selector rows for it', async () => {
    renderProfile({ ...PROFILE, isExternal: true })
    expect(await screen.findByRole('heading', { level: 1, name: 'Ada Lovelace' })).toBeInTheDocument()
    expect(screen.getByText(/created by sync and has never signed in here/)).toBeInTheDocument()
    expect(screen.queryByRole('heading', { level: 2, name: 'Clearance' })).not.toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    // The level the wire carries for a shadow account is not shown either.
    expect(screen.queryByText('SECRET')).not.toBeInTheDocument()
  })

  it('says everything is as of the last sign-in and managed in Keycloak, with no timestamp', async () => {
    renderProfile()
    expect(await screen.findByText(/Recorded at this user's last sign-in/)).toBeInTheDocument()
    expect(screen.getByText(/managed in Keycloak, not in RocketWiki/)).toBeInTheDocument()
  })

  it('shows none of what the admin roster keeps to itself', async () => {
    // Nationality, email and last-seen stay admin-only (design.md §6.2) — the
    // profile must not grow them back by accident.
    const { container } = render(
      <MemoryRouter initialEntries={['/people/u-1']}>
        <UrqlProvider value={createMockUrqlClient((name) => (name === 'UserProfile' ? { userProfile: PROFILE } : undefined)).client}>
          <Routes>
            <Route path="/people/:userId" element={<UserProfilePage />} />
          </Routes>
        </UrqlProvider>
      </MemoryRouter>,
    )
    await screen.findByRole('heading', { level: 1, name: 'Ada Lovelace' })
    expect(container.textContent).not.toMatch(/nationality|email|last seen/i)
  })

  it('says so when no selector categories are configured, rather than rendering an empty table', async () => {
    renderProfile({ ...PROFILE, selectorEligibility: [] })
    expect(await screen.findByText(/No selector categories are configured/)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
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
    await screen.findByRole('table', { name: 'Selector eligibility' })
    await expectNoAxeViolations()
  })

  it('has no axe violations in the unrecorded and external states', async () => {
    renderProfile({ ...PROFILE, clearanceRecorded: false })
    await screen.findByText('Not recorded')
    await expectNoAxeViolations()
  })
})
