import { describe, expect, it } from 'vitest'
import {
  filterRows,
  focusNeighbourhood,
  graphPath,
  graphRows,
  indexGraph,
  markingLegend,
  touchesFocus,
  type GraphNode,
} from '../graphModel'

/**
 * The pure half of the document graph screen (design.md §6.7 / §21.8). The
 * server decides what the caller may see; these functions only arrange it —
 * and the one thing they must never do is invent something the server
 * omitted, which is why the dropped-edge case is pinned rather than left to
 * "would never happen".
 */

const marking = (level: GraphNode['marking']['level'], levelName: string): GraphNode['marking'] => ({
  level,
  levelName,
  eyesOnly: [],
  ukPrefix: true,
  selectors: [],
  label: `UK ${levelName}`,
})

const node = (id: string, title: string, spaceKey = 'PROP', level: GraphNode['marking']['level'] = 'OFFICIAL'): GraphNode => ({
  id,
  title,
  spaceKey,
  slug: id,
  icon: null,
  marking: marking(level, level.replace('_', '-')),
})

const alpha = node('a', 'Alpha')
const bravo = node('b', 'bravo', 'AV', 'SECRET')
const charlie = node('c', 'Charlie', 'PROP', 'OFFICIAL_SENSITIVE')
const delta = node('d', 'Delta')

const nodes = [charlie, alpha, delta, bravo]
const edges = [
  { sourcePageId: 'a', targetPageId: 'c', ordinal: 0 },
  { sourcePageId: 'a', targetPageId: 'b', ordinal: 1 },
  { sourcePageId: 'c', targetPageId: 'b', ordinal: 0 },
]

describe('indexGraph', () => {
  it('keeps outbound links in the page’s own order and sorts inbound ones by title', () => {
    const index = indexGraph(nodes, edges)
    expect(index.outbound.get('a')?.map((n) => n.id)).toEqual(['c', 'b'])
    // Sources of b are a and c; by title, case-insensitively: Alpha, Charlie.
    expect(index.inbound.get('b')?.map((n) => n.id)).toEqual(['a', 'c'])
    expect(index.links).toEqual([
      { sourceId: 'a', targetId: 'c' },
      { sourceId: 'a', targetId: 'b' },
      { sourceId: 'c', targetId: 'b' },
    ])
  })

  it('never draws an edge to a page that is not a node — and never invents the node', () => {
    // The server pins both-endpoints (PageGraph.Induce); this is the client
    // refusing to crash on a payload that broke it, and refusing equally to
    // fill the hole with a placeholder, which is what omission is for.
    const index = indexGraph([alpha], [{ sourcePageId: 'a', targetPageId: 'ghost', ordinal: 0 }])
    expect(index.links).toEqual([])
    expect(index.byId.has('ghost')).toBe(false)
    expect(index.outbound.get('a')).toBeUndefined()
  })

  it('collapses a repeated source→target pair into one link', () => {
    const index = indexGraph(
      [alpha, bravo],
      [
        { sourcePageId: 'a', targetPageId: 'b', ordinal: 0 },
        { sourcePageId: 'a', targetPageId: 'b', ordinal: 3 },
      ],
    )
    expect(index.links).toHaveLength(1)
    expect(index.outbound.get('a')).toHaveLength(1)
  })
})

describe('focusNeighbourhood', () => {
  it('is the focused page and its neighbours in both directions', () => {
    const index = indexGraph(nodes, edges)
    // c links to b and is linked from a — both are neighbours, d is not.
    expect([...focusNeighbourhood(index, 'c')].sort()).toEqual(['a', 'b', 'c'])
  })

  it('is empty when nothing is focused, and when the focused id is not a node', () => {
    const index = indexGraph(nodes, edges)
    expect(focusNeighbourhood(index, null).size).toBe(0)
    // The URL can name any id; a page outside the scope, or one the caller
    // cannot view, is simply not here — and highlighting nothing is what
    // lets the screen say so instead.
    expect(focusNeighbourhood(index, 'ghost').size).toBe(0)
  })
})

describe('touchesFocus', () => {
  it('is true at either end of the link and false with no focus', () => {
    expect(touchesFocus({ sourceId: 'a', targetId: 'b' }, 'a')).toBe(true)
    expect(touchesFocus({ sourceId: 'a', targetId: 'b' }, 'b')).toBe(true)
    expect(touchesFocus({ sourceId: 'a', targetId: 'b' }, 'c')).toBe(false)
    expect(touchesFocus({ sourceId: 'a', targetId: 'b' }, null)).toBe(false)
  })
})

describe('graphRows and filterRows', () => {
  it('lists every page by title with what it links to and what links to it', () => {
    const rows = graphRows(indexGraph(nodes, edges))
    expect(rows.map((r) => r.node.id)).toEqual(['a', 'b', 'c', 'd'])
    expect(rows[0]?.linksTo.map((n) => n.id)).toEqual(['c', 'b'])
    expect(rows[1]?.linkedFrom.map((n) => n.id)).toEqual(['a', 'c'])
    // An isolated page is still a page.
    expect(rows[3]?.linksTo).toEqual([])
    expect(rows[3]?.linkedFrom).toEqual([])
  })

  it('matches a title or a space key, case-insensitively, and everything for a blank filter', () => {
    const rows = graphRows(indexGraph(nodes, edges))
    expect(filterRows(rows, 'av').map((r) => r.node.id)).toEqual(['b'])
    expect(filterRows(rows, 'ALPHA').map((r) => r.node.id)).toEqual(['a'])
    expect(filterRows(rows, '   ')).toHaveLength(4)
  })
})

describe('markingLegend', () => {
  it('names each level present once, in the scheme’s order, with the server’s spelling', () => {
    expect(markingLegend(nodes)).toEqual([
      { level: 'OFFICIAL', levelName: 'OFFICIAL' },
      { level: 'OFFICIAL_SENSITIVE', levelName: 'OFFICIAL-SENSITIVE' },
      { level: 'SECRET', levelName: 'SECRET' },
    ])
  })
})

describe('graphPath', () => {
  it('addresses the whole instance, a scope, a focus and the table view', () => {
    expect(graphPath()).toBe('/graph')
    expect(graphPath({ focus: 'p1' })).toBe('/graph?focus=p1')
    expect(graphPath({ space: 'PROP', focus: 'p1', view: 'table' })).toBe('/graph?space=PROP&focus=p1&view=table')
    // Empty and null mean absent, never a literal "null" in the address.
    expect(graphPath({ space: '', focus: null, view: null })).toBe('/graph')
  })
})
