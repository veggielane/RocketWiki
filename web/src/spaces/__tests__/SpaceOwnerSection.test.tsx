import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render as renderBare, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import type { ReactElement } from 'react'
import { Provider as UrqlProvider } from 'urql'
import { SpaceOwnerSection } from '../SpaceOwnerSection'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { Client, type Exchange, type Operation, CombinedError } from 'urql'
import { filter, map, pipe } from 'wonka'
import { expectNoAxeViolations } from '../../test/axe'

// The owner's name links to their profile (users/UserLink.tsx), and a link
// needs a router to render. Nothing here asserts on navigation.
const render = (ui: ReactElement) => renderBare(<MemoryRouter>{ui}</MemoryRouter>)

/**
 * Who is accountable for a space.
 *
 * Two properties carry the weight. The screen must never imply that ownership
 * grants access — design.md §6.5 keeps them apart, and a UI that put them side
 * by side without saying which is which is the fastest way to have somebody
 * "fix" permissions by reassigning the owner. And `owner: null` is a REAL
 * state, not a loading one: an imported replica arrives ownerless because
 * ownership does not sync, so the gap has to be surfaced rather than spun on.
 */
const OWNER = { id: 'u1', displayName: 'Ada Lovelace', hasAvatar: false }
const DIRECTORY = [
  { id: 'u1', displayName: 'Ada Lovelace', hasAvatar: false },
  { id: 'u2', displayName: 'Grace Hopper', hasAvatar: false },
]

/**
 * The directory answers; the MUTATION fails at the transport.
 *
 * Failing everything would leave the picker empty, so nothing could be chosen
 * and Save would be disabled — the test would pass by never reaching the code
 * it is about.
 */
function mutationFailsClient(): Client {
  const failing: Exchange = () => (ops$) =>
    pipe(
      ops$,
      filter((op: Operation) => op.kind !== 'teardown'),
      map((op: Operation) => {
        const isMutation = op.kind === 'mutation'
        return {
          operation: op,
          data: isMutation ? undefined : { userDirectory: { nodes: DIRECTORY } },
          error: isMutation ? new CombinedError({ networkError: new Error('offline') }) : undefined,
          stale: false,
          hasNext: false,
        }
      }),
    )
  return new Client({ url: '/graphql', exchanges: [failing] })
}

function renderSection({
  owner = OWNER as typeof OWNER | null,
  canManageAccess = true,
  setOwnerResult = { setSpaceOwner: { space: { id: 's1', owner: OWNER }, error: null } } as Record<string, unknown>,
  onChanged = vi.fn(),
} = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'UserDirectory') return { userDirectory: { nodes: DIRECTORY } }
    if (name === 'SetSpaceOwner') return setOwnerResult
    return undefined
  })
  render(
    <UrqlProvider value={mock.client}>
      <SpaceOwnerSection spaceId="s1" owner={owner} canManageAccess={canManageAccess} onChanged={onChanged} />
    </UrqlProvider>,
  )
  return { mock, onChanged }
}

describe('what the section says', () => {
  it('shows the owner to every viewer', () => {
    renderSection({ canManageAccess: false })
    expect(screen.getByText('Ada Lovelace')).toBeInTheDocument()
  })

  it('says ownership is not access, which is the whole point of the field', () => {
    // Without this sentence the control reads as a permission grant, and
    // someone will eventually use it to try to give a colleague access.
    renderSection()
    expect(screen.getByText(/Ownership does not grant access/)).toBeInTheDocument()
  })

  it('never renders the raw owner id', () => {
    // `Space.ownerUserId` is non-null and is the all-zero GUID when ownerless —
    // rendering it would show that as though it were a real person.
    renderSection()
    expect(screen.queryByText(/00000000-0000-0000-0000-000000000000/)).not.toBeInTheDocument()
  })
})

describe('ownerless is a real state, not a loading one', () => {
  it('says so plainly, and to everyone', () => {
    renderSection({ owner: null, canManageAccess: false })
    expect(screen.getByText(/No owner assigned/)).toBeInTheDocument()
  })

  it('explains why an imported space has no owner', () => {
    // Otherwise it reads as a bug rather than as the consequence of ownership
    // not travelling with content.
    renderSection({ owner: null })
    expect(screen.getByText(/ownership does not travel with the content/)).toBeInTheDocument()
  })

  it('offers to fix it when the viewer may, and only then', () => {
    renderSection({ owner: null })
    expect(screen.getByRole('button', { name: 'Assign an owner' })).toBeInTheDocument()
  })

  it('offers no control to a viewer who may not manage the space', () => {
    renderSection({ owner: null, canManageAccess: false })
    expect(screen.getByText(/No owner assigned/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Assign an owner' })).not.toBeInTheDocument()
  })
})

describe('reassignment is gated on canManageAccess alone', () => {
  it('hides the control from a viewer who cannot manage', () => {
    renderSection({ canManageAccess: false })
    expect(screen.queryByRole('button', { name: 'Change owner' })).not.toBeInTheDocument()
  })

  it('sends the chosen user id and refreshes the space', async () => {
    const { mock, onChanged } = renderSection()

    fireEvent.click(screen.getByRole('button', { name: 'Change owner' }))
    const field = screen.getByLabelText('New owner')
    fireEvent.mouseDown(field)
    fireEvent.click(await screen.findByText('Grace Hopper'))
    fireEvent.click(screen.getByRole('button', { name: 'Save owner' }))

    await waitFor(() => expect(onChanged).toHaveBeenCalled())
    const sent = mock.operations.find((o) => o.name === 'SetSpaceOwner')?.variables.input
    expect(sent).toEqual({ spaceId: 's1', ownerUserId: 'u2' })
  })

  it('cannot save before somebody is chosen', async () => {
    renderSection({ owner: null })
    fireEvent.click(screen.getByRole('button', { name: 'Assign an owner' }))
    expect(screen.getByRole('button', { name: 'Save owner' })).toBeDisabled()
  })
})

describe('refusals are designed UX, not raw errors', () => {
  it('shows the server’s own wording for a refusal', async () => {
    // Forbidden / Validation (no such user) / NotFound all arrive this way, and
    // the server's sentence is the one that explains which happened.
    const { onChanged } = renderSection({
      setOwnerResult: {
        setSpaceOwner: {
          space: null,
          error: { __typename: 'ForbiddenError', message: 'instance admin or space admin required' },
        },
      },
    })

    fireEvent.click(screen.getByRole('button', { name: 'Change owner' }))
    fireEvent.mouseDown(screen.getByLabelText('New owner'))
    fireEvent.click(await screen.findByText('Grace Hopper'))
    fireEvent.click(screen.getByRole('button', { name: 'Save owner' }))

    expect(await screen.findByText(/instance admin or space admin required/)).toBeInTheDocument()
    // Nothing changed, so nothing is refetched.
    expect(onChanged).not.toHaveBeenCalled()
  })

  it('says the owner is unchanged when the request never arrived', async () => {
    // A genuine transport failure: no payload at all, so the typed-error
    // helper has nothing to read and the button would otherwise appear to do
    // nothing. Driven through a failing exchange rather than a malformed
    // response, because those are different bugs and only one of them is this.
    const onChanged = vi.fn()
    render(
      <UrqlProvider value={mutationFailsClient()}>
        <SpaceOwnerSection spaceId="s1" owner={OWNER} canManageAccess onChanged={onChanged} />
      </UrqlProvider>,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Change owner' }))
    fireEvent.mouseDown(screen.getByLabelText('New owner'))
    fireEvent.click(await screen.findByText('Grace Hopper'))
    fireEvent.click(screen.getByRole('button', { name: 'Save owner' }))

    expect(await screen.findByText(/this space's owner is unchanged/)).toBeInTheDocument()
    expect(onChanged).not.toHaveBeenCalled()
  })
})

describe('the picker', () => {
  it('opens with names in it rather than an empty box', async () => {
    // A blank search returns the roster's first page; demanding a guess at a
    // spelling before showing anything is a worse way to choose a person.
    renderSection()
    fireEvent.click(screen.getByRole('button', { name: 'Change owner' }))
    fireEvent.mouseDown(screen.getByLabelText('New owner'))

    expect(await screen.findByText('Grace Hopper')).toBeInTheDocument()
  })

  it('asks the directory for identity only', () => {
    // A picker that returned email, groups or nationality would be a second place
    // to learn things about colleagues.
    const { mock } = renderSection()
    fireEvent.click(screen.getByRole('button', { name: 'Change owner' }))
    const document = mock.operations.find((o) => o.name === 'UserDirectory')
    expect(document).toBeDefined()
  })
})

describe('accessibility', () => {
  it('has no axe violations with an owner', async () => {
    renderSection()
    await expectNoAxeViolations()
  })

  it('has no axe violations in the ownerless state', async () => {
    renderSection({ owner: null })
    await expectNoAxeViolations()
  })

  it('has no axe violations with the picker open', async () => {
    renderSection()
    fireEvent.click(screen.getByRole('button', { name: 'Change owner' }))
    await screen.findByLabelText('New owner')
    await expectNoAxeViolations()
  })
})
