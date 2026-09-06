import { useCallback, useEffect, useMemo, useRef } from 'react'
import ForceGraph2D, { type ForceGraphMethods, type LinkObject, type NodeObject } from 'react-force-graph-2d'
import { alpha, useTheme } from '@mui/material/styles'
import { focusNeighbourhood, touchesFocus, type GraphIndex, type GraphLink, type GraphNode } from './graphModel'
import { graphColours } from './graphColours'
import { nodeTooltip } from './nodeTooltip'

/** What both canvases take — the page decides which one to mount. */
export interface GraphCanvasProps {
  index: GraphIndex
  /** The page to frame and highlight, or null. Must be a node of `index` to have any effect. */
  focusId: string | null
  width: number
  height: number
  onNodeSelect: (node: GraphNode) => void
}

type Datum = NodeObject<GraphNode>
type LinkDatum = LinkObject<GraphNode, GraphLink>

/** force-graph draws a node at radius `nodeRelSize · √val`; mirrored in `drawNode` for the ring and label. */
const NODE_REL_SIZE = 4
/** The focused node's `val` — its dot is √5 ≈ 2.2× the radius of any other, a size cue that needs no colour. */
export const FOCUS_VAL = 5
/** Zoom level from which every node gets a label; below it only the focus neighbourhood is labelled. */
const LABEL_ZOOM = 1.6

/**
 * The force-directed canvas (react-force-graph-2d). Not MUI — a canvas has
 * no components — but everything it paints with comes from the theme
 * (graphColours.ts), and everything it knows about the graph comes from the
 * index the page built from the server's own filtered answer.
 *
 * The focused page is set apart three ways at once, none of them colour: a
 * larger dot, a double ring around it, and a bold label that is always drawn
 * while other labels only appear once zoomed in. Colour is used ON TOP of
 * that — the rest of the graph dims — but a monochrome screen still shows
 * which node is the focus. This canvas is mouse-only by nature; the page
 * beside it offers the table view as the keyboard and screen-reader path,
 * and the wrapper the page puts around this canvas says so.
 */
export function GraphCanvas2D({ index, focusId, width, height, onNodeSelect }: GraphCanvasProps) {
  const theme = useTheme()
  const colours = useMemo(() => graphColours(theme), [theme])
  const fgRef = useRef<ForceGraphMethods<Datum, LinkDatum> | undefined>(undefined)

  // force-graph writes layout state (x, y, vx, …) onto the objects it is
  // given and swaps a link's `source`/`target` ids for the node objects, so
  // it gets copies: the query result stays immutable, and `sourceId` /
  // `targetId` survive on the link for `touchesFocus` to read.
  const graphData = useMemo(
    () => ({
      nodes: index.nodes.map((node) => ({ ...node })) as Datum[],
      links: index.links.map((link) => ({ ...link, source: link.sourceId, target: link.targetId })) as LinkDatum[],
    }),
    [index],
  )
  const neighbourhood = useMemo(() => focusNeighbourhood(index, focusId), [index, focusId])
  const dimmed = neighbourhood.size > 0

  // Frame the graph once the simulation has settled — and again whenever the
  // focus changes after that, since a focus change alone does not restart the
  // engine. Before it has settled, positions are still moving and a fit would
  // frame the wrong thing, so the flag gates the focus effect.
  const settled = useRef(false)
  useEffect(() => {
    settled.current = false
  }, [graphData])
  const frame = useCallback(() => {
    const fg = fgRef.current
    if (!fg) return
    if (neighbourhood.size > 0) fg.zoomToFit(600, 80, (node) => neighbourhood.has(String(node.id)))
    else fg.zoomToFit(600, 40)
  }, [neighbourhood])
  useEffect(() => {
    if (settled.current) frame()
  }, [frame])

  const drawNode = useCallback(
    (node: Datum, ctx: CanvasRenderingContext2D, globalScale: number) => {
      const isFocus = node.id === focusId
      const x = node.x ?? 0
      const y = node.y ?? 0
      const r = NODE_REL_SIZE * Math.sqrt(isFocus ? FOCUS_VAL : 1)
      // Stroke widths and ring gaps are divided by the zoom so they stay the
      // same on screen whether the graph is framed whole or zoomed to a page.
      const px = 1 / globalScale

      ctx.beginPath()
      ctx.arc(x, y, r, 0, 2 * Math.PI)
      ctx.lineWidth = px
      ctx.strokeStyle = dimmed && !neighbourhood.has(node.id) ? alpha(colours.nodeStroke(node.marking.level), 0.35) : colours.nodeStroke(node.marking.level)
      ctx.stroke()

      if (isFocus) {
        // A double ring: the outer in the text colour, the inner in the
        // background, so on either theme it reads as a ring with a gap.
        ctx.beginPath()
        ctx.arc(x, y, r + 4 * px, 0, 2 * Math.PI)
        ctx.lineWidth = 2 * px
        ctx.strokeStyle = colours.label
        ctx.stroke()
        ctx.beginPath()
        ctx.arc(x, y, r + 2 * px, 0, 2 * Math.PI)
        ctx.lineWidth = 1.5 * px
        ctx.strokeStyle = colours.focusRingGap
        ctx.stroke()
      }

      const labelled = isFocus || neighbourhood.has(node.id) || globalScale >= LABEL_ZOOM
      if (!labelled) return
      const fontSize = (isFocus ? 13 : 11) * px
      ctx.font = `${isFocus ? '700' : '400'} ${fontSize}px ${theme.typography.fontFamily}`
      ctx.textAlign = 'center'
      ctx.textBaseline = 'top'
      ctx.fillStyle = dimmed && !neighbourhood.has(node.id) ? alpha(colours.label, 0.55) : colours.label
      ctx.fillText(node.title, x, y + r + (isFocus ? 6 : 3) * px)
    },
    [colours, dimmed, focusId, neighbourhood, theme.typography.fontFamily],
  )

  const linkColour = useCallback(
    (link: LinkDatum): string => {
      if (!dimmed) return alpha(colours.link, 0.5)
      return touchesFocus(link, focusId) ? colours.label : alpha(colours.link, 0.15)
    },
    [colours, dimmed, focusId],
  )

  return (
    <ForceGraph2D<GraphNode, GraphLink>
      ref={fgRef}
      width={width}
      height={height}
      backgroundColor={colours.background}
      graphData={graphData}
      nodeId="id"
      nodeRelSize={NODE_REL_SIZE}
      nodeVal={(node) => (node.id === focusId ? FOCUS_VAL : 1)}
      nodeColor={(node) =>
        dimmed && !neighbourhood.has(node.id)
          ? alpha(colours.nodeFill(node.marking.level), 0.35)
          : colours.nodeFill(node.marking.level)
      }
      nodeLabel={(node) => nodeTooltip(node)}
      nodeCanvasObjectMode={() => 'after'}
      nodeCanvasObject={drawNode}
      linkColor={linkColour}
      linkWidth={(link) => (touchesFocus(link, focusId) ? 2 : 1)}
      linkDirectionalArrowLength={4}
      linkDirectionalArrowRelPos={1}
      linkDirectionalArrowColor={linkColour}
      onNodeClick={(node) => onNodeSelect(node)}
      onEngineStop={() => {
        settled.current = true
        frame()
      }}
    />
  )
}
