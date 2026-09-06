import type { ClassificationLevel, GraphNodeFragment, PageGraphQuery } from '../graphql/generated/graphql'

/**
 * The document graph as the SPA reads it (design.md §6.7 / §21.8).
 *
 * Everything in here is pure and works on the wire's own shapes: the server
 * has already decided what the caller may see, so nothing below filters,
 * gates or fills in — a page the caller cannot view is simply not among
 * `nodes`, and an edge touching one is not among `edges`. The one guard,
 * dropping an edge whose endpoint is not a node, exists so a payload that
 * broke the server's both-endpoints invariant cannot crash the canvas; it
 * must never grow into a placeholder for the missing side.
 */
export type GraphNode = GraphNodeFragment
export type GraphEdge = PageGraphQuery['pageGraph']['edges'][number]

/** A directed link between two nodes, in the shape the canvases key on. */
export interface GraphLink {
  sourceId: string
  targetId: string
}

export interface GraphIndex {
  nodes: readonly GraphNode[]
  byId: ReadonlyMap<string, GraphNode>
  /** Distinct source→target pairs, in the wire's source-then-ordinal order. */
  links: readonly GraphLink[]
  /** The pages a node links to, in the page's own first-occurrence order. */
  outbound: ReadonlyMap<string, readonly GraphNode[]>
  /** The pages linking to a node, by title. */
  inbound: ReadonlyMap<string, readonly GraphNode[]>
}

const byTitle = (a: GraphNode, b: GraphNode): number =>
  a.title.localeCompare(b.title, undefined, { sensitivity: 'base' }) || a.id.localeCompare(b.id)

function append(map: Map<string, GraphNode[]>, key: string, node: GraphNode): void {
  const list = map.get(key)
  if (list) list.push(node)
  else map.set(key, [node])
}

export function indexGraph(nodes: readonly GraphNode[], edges: readonly GraphEdge[]): GraphIndex {
  const byId = new Map(nodes.map((node) => [node.id, node]))
  const outbound = new Map<string, GraphNode[]>()
  const inbound = new Map<string, GraphNode[]>()
  const links: GraphLink[] = []
  const seen = new Set<string>()
  for (const edge of edges) {
    const source = byId.get(edge.sourcePageId)
    const target = byId.get(edge.targetPageId)
    if (source === undefined || target === undefined) continue
    const key = `${edge.sourcePageId}>${edge.targetPageId}`
    if (seen.has(key)) continue
    seen.add(key)
    links.push({ sourceId: source.id, targetId: target.id })
    append(outbound, source.id, target)
    append(inbound, target.id, source)
  }
  for (const list of inbound.values()) list.sort(byTitle)
  return { nodes, byId, links, outbound, inbound }
}

const NOBODY: ReadonlySet<string> = new Set()

/**
 * The focused page and every page one link away from it, in either direction
 * — what the canvas frames and labels, and what stays at full strength while
 * the rest of the graph is dimmed. Empty when nothing is focused OR the
 * focused id is not a node of this graph: the second case is the URL naming a
 * page outside the selected space or one the caller cannot view, and the
 * screen says so rather than highlighting nothing in silence.
 */
export function focusNeighbourhood(index: GraphIndex, focusId: string | null): ReadonlySet<string> {
  if (focusId === null || !index.byId.has(focusId)) return NOBODY
  const ids = new Set([focusId])
  for (const node of index.outbound.get(focusId) ?? []) ids.add(node.id)
  for (const node of index.inbound.get(focusId) ?? []) ids.add(node.id)
  return ids
}

/** Whether a link has the focused page at either end. */
export function touchesFocus(link: GraphLink, focusId: string | null): boolean {
  return focusId !== null && (link.sourceId === focusId || link.targetId === focusId)
}

/** One row of the accessible listing: a page, what it links to, and what links to it. */
export interface GraphRow {
  node: GraphNode
  linksTo: readonly GraphNode[]
  linkedFrom: readonly GraphNode[]
}

export function graphRows(index: GraphIndex): GraphRow[] {
  return [...index.nodes].sort(byTitle).map((node) => ({
    node,
    linksTo: index.outbound.get(node.id) ?? [],
    linkedFrom: index.inbound.get(node.id) ?? [],
  }))
}

/** Rows whose title or space key contains the filter, case-insensitively; every row for a blank filter. */
export function filterRows(rows: readonly GraphRow[], filter: string): GraphRow[] {
  const needle = filter.trim().toLowerCase()
  if (needle.length === 0) return [...rows]
  return rows.filter(
    (row) => row.node.title.toLowerCase().includes(needle) || row.node.spaceKey.toLowerCase().includes(needle),
  )
}

/**
 * The one ceiling on how much of the graph the screen takes on at once: the
 * most nodes the canvas will simulate, and the most rows the table renders.
 * One constant on purpose — the table is what the page falls back to when
 * the canvas will not draw, so the two must never disagree about what "too
 * many" means, and there is one number to change.
 *
 * Measured on a seeded instance of 5010 pages and 19148 links: the server
 * answered the whole-instance query in about 0.2 s, and the canvas then drew
 * an unreadable mat of edges while the force simulation held the main thread
 * — a 0 ms timeout took 105 ms to fire, and the Table toggle took 6.7 s to
 * take effect. A single space of 50 pages had no measurable lag. Above this
 * the simulation is not started at all: a hairball that also freezes the tab
 * is worse than no drawing. A table this long is slower to draw than it is
 * useful to read, so it stops here too; the row above it says how many were
 * left out and the filter is the way down to them.
 */
export const GRAPH_NODE_LIMIT = 1000

export interface LegendEntry {
  level: ClassificationLevel
  /** The server's display spelling for the level (`PageMarkingView.levelName`), never composed here. */
  levelName: string
}

/**
 * Display order for the legend — the schema's own declaration order for
 * `ClassificationLevel`, which is also markingTone's table order. Presentation
 * only: nothing here ranks a level against a person, and a level this build
 * has not heard of sorts last rather than being dropped.
 */
const LEVEL_ORDER: readonly ClassificationLevel[] = ['OFFICIAL', 'OFFICIAL_SENSITIVE', 'SECRET', 'TOP_SECRET']

/** The classification levels present among `nodes`, once each, with the spelling the server gave them. */
export function markingLegend(nodes: readonly GraphNode[]): LegendEntry[] {
  const seen = new Map<ClassificationLevel, string>()
  for (const node of nodes) {
    if (!seen.has(node.marking.level)) seen.set(node.marking.level, node.marking.levelName)
  }
  const rank = (level: ClassificationLevel) => {
    const at = LEVEL_ORDER.indexOf(level)
    return at === -1 ? LEVEL_ORDER.length : at
  }
  return [...seen.entries()]
    .map(([level, levelName]) => ({ level, levelName }))
    .sort((a, b) => rank(a.level) - rank(b.level) || a.levelName.localeCompare(b.levelName))
}

export const GRAPH_PATH = '/graph'

export interface GraphLocation {
  /** A space key to scope the graph to; absent means the whole instance, the product's default. */
  space?: string | null
  /** The page to highlight and frame. In the URL so the view can be linked and survives a refresh. */
  focus?: string | null
  /** `table` for the accessible listing; absent for the canvas. */
  view?: 'table' | null
}

/** The graph screen's address for a scope, a focused page and a view. */
export function graphPath({ space, focus, view }: GraphLocation = {}): string {
  const params = new URLSearchParams()
  if (space) params.set('space', space)
  if (focus) params.set('focus', focus)
  if (view === 'table') params.set('view', 'table')
  const query = params.toString()
  return query.length > 0 ? `${GRAPH_PATH}?${query}` : GRAPH_PATH
}
