import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { Provider } from 'urql'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { SpaceTreeNav } from '../SpaceTreeNav'
import { expectNoAxeViolations } from '../../test/axe'

const SPACES = {
  spaces: [
    { id: 'space-1', key: 'ENG', name: 'Engineering', description: null, isReplica: false, originInstanceId: null },
    { id: 'space-2', key: 'OPS', name: 'Operations', description: null, isReplica: true, originInstanceId: 'LOW' },
  ],
}

const node = (
  id: string,
  title: string,
  slug: string,
  children: unknown[] = [],
  icon: string | null = null,
) => ({
  id,
  title,
  slug,
  icon,
  hasRestrictions: false,
  labels: [],
  marking: { level: 'OFFICIAL', levelName: 'Official' },
  children,
})

/**
 * Two roots, so there is always a branch OFF the current page's path to assert
 * stays folded. 'Launch notes' is three deep — the auto-expansion has to open
 * more than one level to reveal 'Igniter trace'.
 */
const TREE = {
  pageTree: [
    node('page-1', 'Launch notes', 'launch-notes', [
      node('page-2', 'Static fire', 'static-fire', [
        node('page-5', 'Igniter trace', 'igniter-trace'),
      ], 'SATELLITE'),
    ], 'ROCKET'),
    node('page-3', 'Runbooks', 'runbooks', [node('page-4', 'Chill-in', 'chill-in')]),
  ],
}

/** Echoes the current URL so a test can assert the picker actually moved us. */
function LocationProbe() {
  return <div data-testid="location">{useLocation().pathname}</div>
}

/**
 * A real in-router navigation. `rerender` with different `initialEntries` will
 * not do: MemoryRouter reads those once on mount, so the route would never
 * change — and remounting would discard the very state under test.
 */
function GoTo({ to }: { to: string }) {
  const navigate = useNavigate()
  return (
    <button type="button" onClick={() => navigate(to)}>
      go to {to}
    </button>
  )
}

function renderNav(path: string) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SpaceList') return SPACES
    if (name === 'SpacePageTree') return TREE
    if (name === 'PageSpaceRef') return { page: { id: 'page-2', spaceId: 'space-1', spaceKey: 'ENG' } }
    return undefined
  })
  render(
    <Provider value={mock.client}>
      <MemoryRouter initialEntries={[path]}>
        <SpaceTreeNav />
        <GoTo to="/spaces/ENG/igniter-trace" />
        <Routes>
          <Route path="*" element={<LocationProbe />} />
        </Routes>
      </MemoryRouter>
    </Provider>,
  )
  return mock
}

const picker = () => screen.getByRole('combobox', { name: 'Space' })
const location = () => screen.getByTestId('location').textContent

describe('SpaceTreeNav space picker', () => {
  it('shows the current space as the chosen value rather than listing every space', async () => {
    renderNav('/spaces/ENG')
    expect(await screen.findByRole('combobox', { name: 'Space' })).toHaveTextContent('Engineering')
    // The other spaces are choices now, not rows competing with the tree.
    expect(screen.queryByRole('link', { name: 'Operations' })).toBeNull()
  })

  it('invites a choice off in the admin screens, rather than reading as "no spaces exist"', async () => {
    renderNav('/settings')
    expect(await screen.findByRole('combobox', { name: 'Space' })).toHaveTextContent('Choose a space')
    // And no tree, because there is no space to draw one for.
    expect(screen.queryByRole('link', { name: 'Launch notes' })).toBeNull()
  })

  function renderEmptySpaceList(isInstanceAdmin: boolean) {
    const mock = createMockUrqlClient((name) => {
      if (name === 'SpaceList') return { spaces: [] }
      if (name === 'CurrentUser')
        return { me: { id: 'sub-1', localUserId: 'u-1', email: 'a@b', name: 'A', hasAvatar: false, isInstanceAdmin } }
      return undefined
    })
    render(
      <Provider value={mock.client}>
        <MemoryRouter initialEntries={['/']}>
          <SpaceTreeNav />
        </MemoryRouter>
      </Provider>,
    )
  }

  it('tells an instance admin to create the first space', async () => {
    renderEmptySpaceList(true)
    // An empty state states the fact AND the consequence (web/README.md).
    expect(await screen.findByText(/No spaces yet — create one to start writing/)).toBeInTheDocument()
  })

  it('does not tell a non-admin to create a space they cannot create', async () => {
    // The rail used to render the admin sentence to everyone, pointing readers
    // at a button that is hidden from them (design.md §6.5.1 — creation is
    // instance-admin-only). Shares its copy with the space list now, so the
    // two surfaces cannot drift apart again.
    renderEmptySpaceList(false)
    expect(await screen.findByText(/an instance admin can create the first one/)).toBeInTheDocument()
    expect(screen.queryByText(/create one to start writing/)).not.toBeInTheDocument()
  })

  it('says so when the caller can view no spaces at all', async () => {
    renderEmptySpaceList(true)
    expect(await screen.findByText(/No spaces yet/)).toBeInTheDocument()
    expect(screen.queryByRole('combobox', { name: 'Space' })).toBeNull()
  })

  it('navigates to the space you choose, so the tree and the page agree on where you are', async () => {
    renderNav('/spaces/ENG')
    fireEvent.mouseDown(await screen.findByRole('combobox', { name: 'Space' }))
    fireEvent.click(screen.getByRole('option', { name: /Operations/ }))
    expect(location()).toBe('/spaces/OPS')
  })

  it('takes you to the space browse page when you re-choose the space you are in', async () => {
    // The old space row was a link you could press at any time; a value-change
    // handler never fires for the value already selected, so without this there
    // is no way back to the space's own page from a page route.
    renderNav('/spaces/ENG/static-fire')
    fireEvent.mouseDown(picker())
    fireEvent.click(screen.getByRole('option', { name: /Engineering/ }))
    expect(location()).toBe('/spaces/ENG')
  })

  it('keeps the replica marking visible without opening the dropdown (design.md §12)', async () => {
    renderNav('/spaces/OPS')
    expect(await screen.findByRole('combobox', { name: 'Space' })).toHaveTextContent(
      'Replica of LOW — read-only',
    )
  })

  it('marks replicas in the list too, so the choice is informed before it is made', async () => {
    renderNav('/spaces/ENG')
    fireEvent.mouseDown(await screen.findByRole('combobox', { name: 'Space' }))
    expect(screen.getByRole('option', { name: /Operations/ })).toHaveTextContent('Replica of LOW — read-only')
    expect(screen.getByRole('option', { name: /Engineering/ })).not.toHaveTextContent('Replica')
  })

  it('resolves the space from a page id, so a /pages/{id} route still picks one', async () => {
    // A page URL carries no space, so the nav has to look it up — otherwise the
    // picker would sit empty the moment you clicked into a page from it.
    const mock = renderNav('/pages/page-2')
    expect(await screen.findByRole('combobox', { name: 'Space' })).toHaveTextContent('Engineering')
    expect(mock.operations.some((op) => op.name === 'PageSpaceRef')).toBe(true)
  })

  it('does not resolve a page reference when the route already names the space', async () => {
    const mock = renderNav('/spaces/ENG')
    await screen.findByRole('combobox', { name: 'Space' })
    expect(mock.operations.some((op) => op.name === 'PageSpaceRef')).toBe(false)
  })

  it('loads one space tree, not one per space', async () => {
    const mock = renderNav('/spaces/ENG')
    await screen.findByRole('link', { name: 'Launch notes' })
    const treeQueries = mock.operations.filter((op) => op.name === 'SpacePageTree')
    expect(treeQueries.every((op) => op.variables['spaceId'] === 'space-1')).toBe(true)
  })

  it('offers a way into the space browser, since /spaces/{key} may not be it', async () => {
    // The browser moved to its own address when the space route began landing on
    // a default page, and its only entry point was space settings — which is not
    // where anyone looks for "show me this space's pages".
    renderNav('/spaces/ENG')
    expect(await screen.findByRole('link', { name: 'Browse all pages' })).toHaveAttribute(
      'href',
      '/spaces/ENG/-/browse',
    )
  })

  it('offers no browse link when no space is chosen', async () => {
    renderNav('/settings')
    await screen.findByRole('combobox', { name: 'Space' })
    expect(screen.queryByRole('link', { name: 'Browse all pages' })).toBeNull()
  })

  it('says so rather than showing nothing when the space has no pages', async () => {
    const mock = createMockUrqlClient((name) => {
      if (name === 'SpaceList') return SPACES
      if (name === 'SpacePageTree') return { pageTree: [] }
      return undefined
    })
    render(
      <Provider value={mock.client}>
        <MemoryRouter initialEntries={['/spaces/ENG']}>
          <SpaceTreeNav />
        </MemoryRouter>
      </Provider>,
    )
    expect(await screen.findByText('No pages yet')).toBeInTheDocument()
  })
})

describe('SpaceTreeNav collapsing', () => {
  it('starts folded at the roots when no page is open', async () => {
    renderNav('/spaces/ENG')
    expect(await screen.findByRole('link', { name: 'Launch notes' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Runbooks' })).toBeInTheDocument()
    // Nothing below them — this is the whole point of the change.
    expect(screen.queryByRole('link', { name: 'Static fire' })).toBeNull()
    expect(screen.queryByRole('link', { name: 'Chill-in' })).toBeNull()
  })

  it('opens every branch on the way to the current page, and only those', async () => {
    renderNav('/spaces/ENG/igniter-trace')
    // Two levels deep: both ancestors had to open for this to be reachable.
    expect(await screen.findByRole('link', { name: 'Igniter trace' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Static fire' })).toBeInTheDocument()
    // The other root is off the path and stays folded.
    expect(screen.queryByRole('link', { name: 'Chill-in' })).toBeNull()
  })

  it('opens the path when you arrived by id, which is what a search result links to', async () => {
    renderNav('/pages/page-5')
    expect(await screen.findByRole('link', { name: 'Igniter trace' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Chill-in' })).toBeNull()
  })

  it('expands the current page too, so you can see where you can go from here', async () => {
    // "Collapsed to your path" means the path you are ON, not the path up to
    // you: landing on a page reveals what is under it without a click. Its own
    // children are open; everything off the path stays folded.
    renderNav('/spaces/ENG/launch-notes')
    expect(await screen.findByRole('link', { name: 'Launch notes' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Static fire' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Collapse Launch notes' })).toBeInTheDocument()
    // One level, not the whole subtree — the grandchild stays folded.
    expect(screen.queryByRole('link', { name: 'Igniter trace' })).toBeNull()
    // And it folds, so the reveal is a default rather than something forced on you.
    fireEvent.click(screen.getByRole('button', { name: 'Collapse Launch notes' }))
    expect(screen.queryByRole('link', { name: 'Static fire' })).toBeNull()
  })

  it('lets an off-path branch be opened by hand', async () => {
    renderNav('/spaces/ENG/static-fire')
    const toggle = await screen.findByRole('button', { name: 'Expand Runbooks' })
    expect(screen.queryByRole('link', { name: 'Chill-in' })).toBeNull()
    fireEvent.click(toggle)
    expect(screen.getByRole('link', { name: 'Chill-in' })).toBeInTheDocument()
    // And folded again by the same control, which now names the other action.
    fireEvent.click(screen.getByRole('button', { name: 'Collapse Runbooks' }))
    expect(screen.queryByRole('link', { name: 'Chill-in' })).toBeNull()
  })

  it('exposes open/closed state to assistive tech, not just to the eye', async () => {
    // WCAG 1.4.1: which chevron is drawn cannot be the only signal.
    renderNav('/spaces/ENG/static-fire')
    expect(await screen.findByRole('button', { name: 'Collapse Launch notes' })).toHaveAttribute(
      'aria-expanded',
      'true',
    )
    expect(screen.getByRole('button', { name: 'Expand Runbooks' })).toHaveAttribute('aria-expanded', 'false')
  })

  it('gives a page with no children no disclosure control at all', async () => {
    // A control that opens nothing is worse than none.
    renderNav('/spaces/ENG/igniter-trace')
    await screen.findByRole('link', { name: 'Igniter trace' })
    expect(screen.queryByRole('button', { name: /Igniter trace/ })).toBeNull()
  })

  it('survives a re-render that changes nothing', async () => {
    // A query settling or a keystroke elsewhere in the app must not reset what
    // the user opened by hand.
    const mock = createMockUrqlClient((name) => {
      if (name === 'SpaceList') return SPACES
      if (name === 'SpacePageTree') return TREE
      return undefined
    })
    const tree = (
      <Provider value={mock.client}>
        <MemoryRouter initialEntries={['/spaces/ENG/launch-notes']}>
          <SpaceTreeNav />
        </MemoryRouter>
      </Provider>
    )
    const { rerender } = render(tree)
    fireEvent.click(await screen.findByRole('button', { name: 'Expand Runbooks' }))
    expect(screen.getByRole('link', { name: 'Chill-in' })).toBeInTheDocument()

    rerender(tree)
    expect(screen.getByRole('link', { name: 'Chill-in' })).toBeInTheDocument()
  })

  it('opens the new path on navigation without folding a branch you opened by hand', async () => {
    // Expansion is half route, half user. Navigating is not a request to tidy
    // up the tree behind you, so the two must compose rather than one winning.
    renderNav('/spaces/ENG/launch-notes')
    fireEvent.click(await screen.findByRole('button', { name: 'Expand Runbooks' }))
    expect(screen.getByRole('link', { name: 'Chill-in' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'go to /spaces/ENG/igniter-trace' }))

    // The path to the new page opened...
    expect(location()).toBe('/spaces/ENG/igniter-trace')
    expect(screen.getByRole('link', { name: 'Igniter trace' })).toBeInTheDocument()
    // ...and the branch the user opened is still open.
    expect(screen.getByRole('link', { name: 'Chill-in' })).toBeInTheDocument()
  })
})

describe('SpaceTreeNav rows', () => {
  it("draws each page's own icon, and the generic page glyph for one without", async () => {
    renderNav('/spaces/ENG/static-fire')
    const iconed = await screen.findByRole('link', { name: 'Launch notes' })
    const plain = screen.getByRole('link', { name: 'Runbooks' })
    // MUI's own dev-only test id: the glyph is deliberately decorative (the
    // title beside it names the page), so there is no accessible name to
    // query it by — and adding one purely for this assertion would make a
    // screen reader read every row twice.
    expect(within(iconed).getByTestId('RocketLaunchOutlinedIcon')).toBeInTheDocument()
    expect(within(plain).getByTestId('ArticleOutlinedIcon')).toBeInTheDocument()
  })

  it('falls back to the generic glyph for an icon this build has never heard of', async () => {
    // design.md §12: a page can arrive from an instance whose icon set is
    // ahead of this build's. It renders as an ordinary page, not a hole.
    renderNav('/spaces/ENG/static-fire')
    const unknown = await screen.findByRole('link', { name: 'Static fire' })
    expect(within(unknown).getByTestId('ArticleOutlinedIcon')).toBeInTheDocument()
  })

  it('links every page by its readable address, at any depth', async () => {
    renderNav('/spaces/ENG/static-fire')
    expect(await screen.findByRole('link', { name: 'Launch notes' }))
      .toHaveAttribute('href', '/spaces/ENG/launch-notes')
    // The hierarchy is absent from the URL on purpose, so a child's address is
    // no longer than its parent's — that is what lets a page be moved.
    expect(screen.getByRole('link', { name: 'Static fire' }))
      .toHaveAttribute('href', '/spaces/ENG/static-fire')
  })

  it('marks the page you are on when you arrived by its slug', async () => {
    // The ordinary route now. Matching on slug rather than id is what keeps
    // this working without a second query to turn the slug into an id.
    renderNav('/spaces/ENG/static-fire')
    const active = await screen.findByRole('link', { name: 'Static fire' })
    expect(active).toHaveClass('Mui-selected')
    expect(screen.getByRole('link', { name: 'Runbooks' })).not.toHaveClass('Mui-selected')
  })

  it('still marks it when you arrived by id, which is what a search result links to', async () => {
    renderNav('/pages/page-2')
    const active = await screen.findByRole('link', { name: 'Static fire' })
    expect(active).toHaveClass('Mui-selected')
  })

  it('marks nothing in the tree on a space system page', async () => {
    // /spaces/ENG/-/admin is not a page, and `-` can never be a slug, so the
    // slug arm must not go looking for a node to highlight.
    renderNav('/spaces/ENG/-/admin')
    const runbooks = await screen.findByRole('link', { name: 'Runbooks' })
    expect(runbooks).not.toHaveClass('Mui-selected')
    expect(screen.getByRole('link', { name: 'Launch notes' })).not.toHaveClass('Mui-selected')
  })

  it('tells the cache what changes the tree, so a created page appears without a reload', async () => {
    // The bug this pins: SpacePageTree returns PageTreeNode, no mutation returns
    // one, and urql's document cache invalidates on shared typenames — so nothing
    // could ever refresh the sidebar and a new page simply was not in it.
    const mock = renderNav('/spaces/ENG')
    await screen.findByRole('link', { name: 'Launch notes' })

    const tree = mock.operations.find((op) => op.name === 'SpacePageTree')
    expect(tree?.additionalTypenames).toContain('Page')
  })

  it('has no axe violations with the picker, a nested tree and its disclosures', async () => {
    // The tree nests <li> inside <li>; a list item outside a list element is a
    // violation, and it is exactly what a first pass at this component shipped.
    // The disclosure buttons are the new hazard: a button inside the row's
    // anchor would be "nested-interactive".
    renderNav('/spaces/ENG/static-fire')
    await screen.findByRole('link', { name: 'Static fire' })
    await expectNoAxeViolations()

    // And with the picker open — the options portal out of the drawer.
    fireEvent.mouseDown(picker())
    await screen.findByRole('option', { name: /Operations/ })
    await expectNoAxeViolations()
  })
})

/**
 * A GraphQL document has to stop at some depth, so the tree is always truncated
 * somewhere. `hasChildren` is what keeps the truncation honest, and the subtree
 * query is what makes the chevron it draws lead somewhere.
 */
describe('SpaceTreeNav deeper than the query reaches', () => {
  const truncated = {
    pageTree: [
      {
        id: 'deep-1',
        title: 'Handbook',
        slug: 'handbook',
        icon: null,
        // The server says there are children; the document did not carry them.
        hasChildren: true,
        children: [],
        hasRestrictions: false,
        labels: [],
        marking: { level: 'OFFICIAL', levelName: 'Official' },
      },
    ],
  }
  const subtree = {
    pageSubtree: [
      {
        id: 'deep-2',
        title: 'Onboarding',
        slug: 'onboarding',
        icon: null,
        hasChildren: false,
        children: [],
        hasRestrictions: false,
        labels: [],
        marking: { level: 'OFFICIAL', levelName: 'Official' },
      },
    ],
  }

  function renderTruncated(subtreeResult: Record<string, unknown> | undefined = subtree) {
    const mock = createMockUrqlClient((name) => {
      if (name === 'SpaceList') return SPACES
      if (name === 'SpacePageTree') return truncated
      if (name === 'PageSubtree') return subtreeResult
      return undefined
    })
    render(
      <Provider value={mock.client}>
        <MemoryRouter initialEntries={['/spaces/ENG']}>
          <SpaceTreeNav />
        </MemoryRouter>
      </Provider>,
    )
    return mock
  }

  it('draws a chevron from hasChildren, not from what arrived', async () => {
    // The bug this replaces: with children absent, the tree drew no control at
    // all — a positive claim that the page is a leaf, made about a page that
    // has children the document simply did not reach.
    renderTruncated()
    expect(await screen.findByRole('button', { name: 'Expand Handbook' })).toBeInTheDocument()
  })

  it('fetches the children when the branch is opened', async () => {
    const mock = renderTruncated()
    fireEvent.click(await screen.findByRole('button', { name: 'Expand Handbook' }))

    expect(await screen.findByRole('link', { name: 'Onboarding' })).toBeInTheDocument()
    const call = mock.operations.find((op) => op.name === 'PageSubtree')
    expect(call?.variables).toEqual({ spaceId: 'space-1', pageId: 'deep-1' })
  })

  it('fetches nothing until the branch is actually opened', async () => {
    // Lazily, or a shallow query would just become a deep one issued in pieces.
    const mock = renderTruncated()
    await screen.findByRole('button', { name: 'Expand Handbook' })
    expect(mock.operations.some((op) => op.name === 'PageSubtree')).toBe(false)
  })

  it('renders nothing extra when the subtree comes back empty', async () => {
    // Every child pruned reads the same as no children (§6.7) — it does not
    // report having expected more than it got.
    renderTruncated({ pageSubtree: [] })
    fireEvent.click(await screen.findByRole('button', { name: 'Expand Handbook' }))
    expect(await screen.findByRole('button', { name: 'Collapse Handbook' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Onboarding' })).toBeNull()
  })
})
