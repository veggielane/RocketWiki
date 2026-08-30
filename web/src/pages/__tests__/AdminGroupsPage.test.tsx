import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { Client, Provider as UrqlProvider, type Exchange, type Operation, CombinedError } from 'urql'
import { filter, map, pipe } from 'wonka'
import { AdminGroupsPage } from '../AdminGroupsPage'
import { AdminPage } from '../AdminPage'
import { MemoryRouter } from 'react-router-dom'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * The groups an instance has heard of.
 *
 * The load-bearing property is not the table — it is that the screen never
 * implies it knows who is in a group. Membership lives in the identity
 * provider; this list is names observed in sign-in tokens, which is why an
 * access rule can be written against one and why a group can be legitimately
 * missing.
 */

/** A client whose every operation fails at the transport, for the error branch. */
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

function renderGroups(groups: string[] | undefined) {
  const mock = createMockUrqlClient((name) => (name === 'KnownGroups' ? { groups } : undefined))
  render(
    <UrqlProvider value={mock.client}>
      <AdminGroupsPage />
    </UrqlProvider>,
  )
  return mock
}

describe('AdminGroupsPage', () => {
  it('lists the group names the instance has seen', async () => {
    renderGroups(['engineering', 'propulsion', 'safety'])

    expect(await screen.findByText('engineering')).toBeInTheDocument()
    expect(screen.getByText('propulsion')).toBeInTheDocument()
    expect(screen.getByText('safety')).toBeInTheDocument()
  })

  it('says where the names come from, so a missing group is not a mystery', () => {
    // An admin who reads this as "the list of groups" will eventually wonder
    // why a real group is absent. The answer belongs on the page.
    renderGroups([])

    expect(screen.getByText(/seen in a sign-in token/)).toBeInTheDocument()
    expect(screen.getByText(/Membership lives in the identity provider/)).toBeInTheDocument()
  })

  it('never claims to know who is in a group', () => {
    // The prose does mention membership — to say it is NOT stored here — so the
    // assertion is about the table, which is where a claim would actually be
    // made: one column of names, no member count, nothing to click into.
    renderGroups(['engineering'])

    const headers = screen.getAllByRole('columnheader').map((h) => h.textContent)
    expect(headers).toEqual(['Group'])
    expect(screen.queryByRole('link', { name: 'engineering' })).not.toBeInTheDocument()
  })

  it('explains an empty list as "nobody has signed in yet", not as an error', () => {
    renderGroups([])

    expect(screen.getByText(/No groups yet/)).toBeInTheDocument()
    expect(screen.queryByRole('grid')).not.toBeInTheDocument()
  })

  it('keeps its heading and renders no empty table when the read fails', async () => {
    render(
      <UrqlProvider value={createFailingUrqlClient()}>
        <AdminGroupsPage />
      </UrqlProvider>,
    )

    expect(await screen.findByText(/Couldn't load the group list/)).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1, name: 'Groups' })).toBeInTheDocument()
    expect(screen.queryByRole('grid')).not.toBeInTheDocument()
    // An error is not an empty result.
    expect(screen.queryByText(/No groups yet/)).not.toBeInTheDocument()
  })

  it('has no axe violations with rows rendered', async () => {
    renderGroups(['engineering', 'propulsion'])
    await screen.findByText('engineering')
    await expectNoAxeViolations()
  })
})

describe('the admin index offers the screen', () => {
  it('links Groups from the admin page', () => {
    render(
      <MemoryRouter>
        <AdminPage />
      </MemoryRouter>,
    )

    expect(screen.getByRole('link', { name: /Groups/ })).toHaveAttribute('href', '/admin/groups')
  })
})
