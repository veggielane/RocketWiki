import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { Client, CombinedError, Provider as UrqlProvider, type Exchange, type Operation } from 'urql'
import { filter, map, pipe } from 'wonka'
import { GraphPage } from '../GraphPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * The document graph screen (design.md §6.7 / §21.8).
 *
 * The canvases are stubbed: jsdom has no canvas, no WebGL and no layout, so
 * the real renderers cannot mount here. Each stub keeps the LAST props its
 * renderer received, so a test can ask the accessors the page hands the
 * canvas — "how big is this node", "how heavy is this link" — the very
 * questions the renderer asks, and can raise a node click the way the
 * renderer would. The 3D stub's module factory also records that it ran at
 * all, which is what pins the lazy load: the factory runs when the module is
 * first imported, and it must not be before the viewer asks for 3D.
 */
const canvases = vi.hoisted(() => ({
  props2d: null as Record<string, unknown> | null,
  props3d: null as Record<string, unknown> | null,
  loaded3d: false,
}))

vi.mock('react-force-graph-2d', () => ({
  default: (props: Record<string, unknown>) => {
    canvases.props2d = props
    return <div data-testid="canvas-2d" />
  },
}))

vi.mock('react-force-graph-3d', () => {
  canvases.loaded3d = true
  return {
    default: (props: Record<string, unknown>) => {
      canvases.props3d = props
      return <div data-testid="canvas-3d" />
    },
  }
})

type Accessor<T> = (arg: T) => number

const marking = (level: string, levelName: string) => ({
  level,
  levelName,
  eyesOnly: [] as string[],
  ukPrefix: true,
  selectors: [] as { category: string; value: string }[],
  label: `UK ${levelName}`,
})

const nodes = [
  { id: 'page-1', title: 'Stage two ignition anomaly review', spaceKey: 'PROP', slug: 'stage-two', icon: 'ROCKET', marking: marking('SECRET', 'SECRET') },
  { id: 'page-2', title: 'Chill-in procedure', spaceKey: 'PROP', slug: 'chill-in', icon: null, marking: marking('OFFICIAL', 'OFFICIAL') },
  { id: 'page-3', title: 'Avionics harness map', spaceKey: 'AV', slug: 'harness-map', icon: 'MAP', marking: marking('OFFICIAL_SENSITIVE', 'OFFICIAL-SENSITIVE') },
  { id: 'page-4', title: 'Orphan note', spaceKey: 'AV', slug: 'orphan-note', icon: null, marking: marking('OFFICIAL', 'OFFICIAL') },
]
const edges = [
  { sourcePageId: 'page-1', targetPageId: 'page-2', ordinal: 0 },
  { sourcePageId: 'page-1', targetPageId: 'page-3', ordinal: 1 },
  { sourcePageId: 'page-3', targetPageId: 'page-2', ordinal: 0 },
]
const spaces = [
  { id: 's-prop', key: 'PROP', name: 'Propulsion', description: null, isReplica: false, originInstanceId: 'LOW', viewerHasAccess: true },
  { id: 's-av', key: 'AV', name: 'Avionics', description: null, isReplica: false, originInstanceId: 'LOW', viewerHasAccess: true },
]

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="location">{`${location.pathname}${location.search}`}</div>
}

function mount(client: Client, path: string) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <UrqlProvider value={client}>
        <Routes>
          <Route path="/graph" element={<GraphPage />} />
          <Route path="*" element={null} />
        </Routes>
        <LocationProbe />
      </UrqlProvider>
    </MemoryRouter>,
  )
}

function renderGraph({ path = '/graph', graph = { nodes, edges } } = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'PageGraph') return { pageGraph: graph }
    if (name === 'SpaceList') return { spaces }
    return undefined
  })
  mount(mock.client, path)
  return mock
}

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

const graphReads = (mock: ReturnType<typeof renderGraph>) =>
  mock.operations.filter((op) => op.name === 'PageGraph').map((op) => op.variables)

const location = () => screen.getByTestId('location').textContent

beforeEach(() => {
  canvases.props2d = null
  canvases.props3d = null
})

describe('GraphPage accessibility', () => {
  it('has no axe violations in the canvas view with a focused page', async () => {
    renderGraph({ path: '/graph?focus=page-1' })
    await screen.findByText('Focused: Stage two ignition anomaly review')
    expect(screen.getByRole('img', { name: /Document graph: 4 pages and 3 links across the instance, focused on Stage two/ })).toBeInTheDocument()
    await expectNoAxeViolations()
  })

  it('has no axe violations in the table view', async () => {
    renderGraph({ path: '/graph?view=table&focus=page-1' })
    await screen.findByRole('table', { name: 'Pages and their links' })
    await expectNoAxeViolations()
  })
})

describe('GraphPage scope', () => {
  it('asks for the whole instance by default, and for the chosen space once one is picked', async () => {
    const mock = renderGraph()
    await screen.findByTestId('canvas-2d')
    // Null on the wire, explicitly: the product's default is everything.
    expect(graphReads(mock)[0]).toEqual({ spaceKey: null })

    const picker = await screen.findByRole('combobox', { name: 'Space' })
    fireEvent.mouseDown(picker)
    fireEvent.click(await screen.findByRole('option', { name: 'Propulsion' }))

    await waitFor(() => expect(graphReads(mock).at(-1)).toEqual({ spaceKey: 'PROP' }))
    // The scope lives in the URL, so the filtered view can be linked and refreshed.
    expect(location()).toBe('/graph?space=PROP')
  })

  it('says the same thing for every empty answer, and offers the way back out', async () => {
    // A space the caller cannot enter, an archived one and a key naming
    // nothing all come back as the same empty graph (design.md §21.8); the
    // screen must not sharpen that into any one of them.
    renderGraph({ path: '/graph?space=NOPE', graph: { nodes: [], edges: [] } })
    expect(await screen.findByText(/Nothing to draw — there are no pages you can view in NOPE/)).toBeInTheDocument()
    expect(screen.queryByText(/protected|cannot enter|archived|does not exist/i)).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Show the whole instance' }))
    expect(location()).toBe('/graph')
  })

  it('reports a failed read as a failure, not as an empty graph', async () => {
    mount(createFailingUrqlClient(), '/graph')
    expect(await screen.findByText("Couldn't load the document graph.")).toBeInTheDocument()
    expect(screen.queryByText(/Nothing to draw/)).not.toBeInTheDocument()
  })
})

describe('GraphPage focus', () => {
  it('reads the focused page from the URL, names it, and hands the canvas a larger node and heavier links for it', async () => {
    renderGraph({ path: '/graph?focus=page-1' })
    await screen.findByText('Focused: Stage two ignition anomaly review')

    const nodeVal = canvases.props2d?.nodeVal as Accessor<{ id: string }>
    const linkWidth = canvases.props2d?.linkWidth as Accessor<{ sourceId: string; targetId: string }>
    // Size, not colour, is what tells the focused node apart (WCAG 1.4.1).
    expect(nodeVal({ id: 'page-1' })).toBeGreaterThan(nodeVal({ id: 'page-2' }))
    // Its links are drawn heavier than links elsewhere in the graph.
    expect(linkWidth({ sourceId: 'page-1', targetId: 'page-2' })).toBeGreaterThan(
      linkWidth({ sourceId: 'page-3', targetId: 'page-2' }),
    )
  })

  it('clears the focus from the URL', async () => {
    renderGraph({ path: '/graph?focus=page-1' })
    fireEvent.click(await screen.findByRole('button', { name: 'Clear focus' }))
    expect(location()).toBe('/graph')
    expect(screen.queryByText(/^Focused:/)).not.toBeInTheDocument()
  })

  it('says when the focused page is not in this graph, without saying why', async () => {
    // Outside the selected space, or not viewable: the sentence names both
    // possibilities and confirms neither (design.md §6.7).
    renderGraph({ path: '/graph?focus=page-nope' })
    expect(await screen.findByText(/The focused page isn't in this graph/)).toBeInTheDocument()
    expect(screen.queryByText(/^Focused:/)).not.toBeInTheDocument()
    const nodeVal = canvases.props2d?.nodeVal as Accessor<{ id: string }>
    // And nothing on the canvas is enlarged for it.
    expect(nodeVal({ id: 'page-1' })).toBe(nodeVal({ id: 'page-2' }))
  })

  it('opens the page when its node is clicked, by its readable address', async () => {
    renderGraph()
    await screen.findByTestId('canvas-2d')
    const onNodeClick = canvases.props2d?.onNodeClick as (node: (typeof nodes)[number]) => void
    onNodeClick(nodes[1]!)
    await waitFor(() => expect(location()).toBe('/spaces/PROP/chill-in'))
  })
})

describe('GraphPage table view — the accessible equivalent', () => {
  it('lists every page with what it links to and what links to it, and marks the focused row', async () => {
    renderGraph({ path: '/graph?view=table&focus=page-2' })
    const table = await screen.findByRole('table', { name: 'Pages and their links' })

    const stageTwo = within(table).getByRole('row', { name: /Stage two ignition anomaly review/ })
    const stageTwoLinksTo = within(stageTwo).getByRole('list', { name: 'Links to 2 pages' })
    expect(within(stageTwoLinksTo).getByRole('link', { name: 'Chill-in procedure' })).toHaveAttribute('href', '/spaces/PROP/chill-in')
    expect(within(stageTwoLinksTo).getByRole('link', { name: 'Avionics harness map' })).toHaveAttribute('href', '/spaces/AV/harness-map')
    // Nothing links to it — said as a word, not left as a blank cell.
    expect(within(stageTwo).getByText('None')).toBeInTheDocument()

    const chillIn = within(table).getByRole('row', { name: /Chill-in procedure/ })
    const chillInLinkedFrom = within(chillIn).getByRole('list', { name: 'Linked from 2 pages' })
    expect(within(chillInLinkedFrom).getAllByRole('link').map((a) => a.textContent)).toEqual([
      'Avionics harness map',
      'Stage two ignition anomaly review',
    ])
    // The focused row is told apart by more than colour: a labelled icon and aria-current.
    expect(chillIn).toHaveAttribute('aria-current', 'true')
    expect(within(chillIn).getByRole('img', { name: 'Focused page' })).toBeInTheDocument()
    expect(stageTwo).not.toHaveAttribute('aria-current')

    // The level rides with each row as text, the way the tree badges it.
    expect(within(stageTwo).getByText('SECRET')).toBeInTheDocument()
    // An isolated page is still listed.
    expect(within(table).getByRole('row', { name: /Orphan note/ })).toBeInTheDocument()
    // No canvas, no legend, no dimensions toggle: nothing in this view needs a pointer.
    expect(screen.queryByTestId('canvas-2d')).not.toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Dimensions' })).not.toBeInTheDocument()
  })

  it('filters the rows by title or space key', async () => {
    renderGraph({ path: '/graph?view=table' })
    await screen.findByRole('table', { name: 'Pages and their links' })
    expect(screen.getAllByRole('row')).toHaveLength(5) // header + 4

    fireEvent.change(screen.getByRole('textbox', { name: 'Filter pages' }), { target: { value: 'av' } })
    expect(screen.getAllByRole('row')).toHaveLength(3)
    expect(screen.getByRole('status')).toHaveTextContent('2 of 4 pages match.')

    fireEvent.change(screen.getByRole('textbox', { name: 'Filter pages' }), { target: { value: 'chill' } })
    expect(screen.getAllByRole('row')).toHaveLength(2)
  })

  it('keeps the view in the URL, so the accessible view is as linkable as the canvas', async () => {
    renderGraph({ path: '/graph?focus=page-1' })
    await screen.findByTestId('canvas-2d')
    fireEvent.click(screen.getByRole('button', { name: 'Table' }))
    expect(location()).toBe('/graph?focus=page-1&view=table')
    expect(await screen.findByRole('table', { name: 'Pages and their links' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Graph' }))
    expect(location()).toBe('/graph?focus=page-1')
  })
})

describe('GraphPage dimensions', () => {
  it('loads the 3D renderer only when asked for it', async () => {
    renderGraph({ path: '/graph?focus=page-1' })
    await screen.findByTestId('canvas-2d')
    // three.js must not ride along with the page: the module has not been
    // touched until the toggle is used.
    expect(canvases.loaded3d).toBe(false)
    expect(screen.queryByTestId('canvas-3d')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '3D' }))
    expect(await screen.findByTestId('canvas-3d')).toBeInTheDocument()
    expect(canvases.loaded3d).toBe(true)
    expect(screen.queryByTestId('canvas-2d')).not.toBeInTheDocument()

    // The same focus rule holds in three dimensions.
    const nodeVal = canvases.props3d?.nodeVal as Accessor<{ id: string }>
    expect(nodeVal({ id: 'page-1' })).toBeGreaterThan(nodeVal({ id: 'page-2' }))
  })
})
