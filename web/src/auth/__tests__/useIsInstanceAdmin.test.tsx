import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { useIsInstanceAdmin } from '../useIsInstanceAdmin'
import { createMockUrqlClient } from '../../test/mockUrqlClient'

function Probe() {
  const { isInstanceAdmin, loading } = useIsInstanceAdmin()
  if (loading) return <div>loading</div>
  return <div>{isInstanceAdmin ? 'admin' : 'not-admin'}</div>
}

function renderProbe(me: Record<string, unknown> | undefined) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'CurrentUser' && me) return { me }
    return undefined
  })
  render(
    <UrqlProvider value={mock.client}>
      <Probe />
    </UrqlProvider>,
  )
}

const baseMe = {
  id: 'sub-1',
  email: 'ada@example.test',
  name: 'Ada',
  groups: [],
  isAuthenticated: true,
  isInstanceAdmin: false,
  localUserId: 'user-1',
}

/**
 * The hook seam is kept, but the answer now comes from the server-computed
 * `CurrentUser.isInstanceAdmin` flag rather than a client-side decode of
 * the token's roles claim — pinned here so it can't quietly revert.
 */
describe('useIsInstanceAdmin', () => {
  it('reports true when the server says isInstanceAdmin', async () => {
    renderProbe({ ...baseMe, isInstanceAdmin: true })
    expect(await screen.findByText('admin')).toBeInTheDocument()
  })

  it('reports false when the server says not an admin', async () => {
    renderProbe(baseMe)
    expect(await screen.findByText('not-admin')).toBeInTheDocument()
  })

  it('fails closed when the query returns nothing', async () => {
    renderProbe(undefined)
    expect(await screen.findByText('not-admin')).toBeInTheDocument()
  })
})
