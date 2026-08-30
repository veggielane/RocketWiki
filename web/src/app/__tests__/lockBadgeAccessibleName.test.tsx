import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider } from 'urql'
import { SpaceTreeNav } from '../SpaceTreeNav'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * "Restriction lock-badges on restricted pages" is a stated invariant, and it
 * was being delivered to sighted users only.
 *
 * MUI's SvgIcon sets `aria-hidden` unless `titleAccess` is passed
 * (`SvgIcon.js`: `"aria-hidden": titleAccess ? undefined : true`), so the
 * `aria-label` both call sites used was inert — a restricted page and an open
 * one sounded identical. axe cannot flag it, because a label on an aria-hidden
 * node is not a violation; the assertion has to be about the ACCESSIBLE NAME,
 * which is what this file makes.
 *
 * The badge sits INSIDE the row's link, so naming it changes the link's own
 * accessible name to "Restricted page This page has access restrictions" — which is the
 * outcome we want (the restriction is announced with the page, not as a
 * separate thing to go looking for) and is what these tests assert.
 */

const SPACES = {
  spaces: [
    { id: 'space-1', key: 'ENG', name: 'Engineering', description: null, isReplica: false, originInstanceId: null },
  ],
}

const node = (id: string, title: string, slug: string, hasRestrictions: boolean) => ({
  id,
  title,
  slug,
  icon: null,
  hasChildren: false,
  hasRestrictions,
  labels: [],
  marking: { level: 'OFFICIAL', levelName: 'Official' },
  children: [],
})

function renderTree() {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceList') return SPACES
    if (name === 'SpacePageTree')
      return {
        pageTree: [
          node('page-1', 'Restricted page', 'restricted-page', true),
          node('page-2', 'Open page', 'open-page', false),
        ],
      }
    return undefined
  })
  render(
    <Provider value={mock.client}>
      <MemoryRouter initialEntries={['/spaces/ENG']}>
        <SpaceTreeNav />
      </MemoryRouter>
    </Provider>,
  )
}

describe('restriction lock badge', () => {
  it('announces the restriction as part of the page it applies to', async () => {
    renderTree()
    const restricted = await screen.findByRole('link', { name: /Restricted page/ })
    expect(restricted).toHaveAccessibleName('Restricted page This page has access restrictions')
  })

  it('leaves an unrestricted page unmarked', async () => {
    renderTree()
    const open = await screen.findByRole('link', { name: /Open page/ })
    expect(open).toHaveAccessibleName('Open page')
  })

  it('exposes the badge itself, rather than hiding it behind aria-hidden', async () => {
    // `getByRole('img', …)` only resolves when the icon is NOT aria-hidden —
    // which is precisely what `titleAccess` fixes and `aria-label` did not.
    renderTree()
    await screen.findByRole('link', { name: /Restricted page/ })
    expect(screen.getAllByRole('img', { name: 'This page has access restrictions' })).toHaveLength(1)
  })

  it('has no axe violations', async () => {
    renderTree()
    await screen.findByRole('link', { name: /Restricted page/ })
    await expectNoAxeViolations()
  })
})
