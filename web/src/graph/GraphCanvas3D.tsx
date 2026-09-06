import { useCallback, useEffect, useMemo, useRef } from 'react'
import ForceGraph3D, { type ForceGraphMethods, type LinkObject, type NodeObject } from 'react-force-graph-3d'
import { useTheme } from '@mui/material/styles'
import { focusNeighbourhood, touchesFocus, type GraphLink, type GraphNode } from './graphModel'
import { graphColours } from './graphColours'
import { nodeTooltip } from './nodeTooltip'
import type { GraphCanvasProps } from './GraphCanvas2D'

type Datum = NodeObject<GraphNode>
type LinkDatum = LinkObject<GraphNode, GraphLink>

/** Volume ∝ val, so the focused sphere is ∛10 ≈ 2.2× the radius of any other. */
const FOCUS_VAL = 10
/** How far the camera settles from the focused page, in graph units. */
const FOCUS_DISTANCE = 160

/**
 * The three-dimensional canvas (react-force-graph-3d, which pulls in
 * three.js). This module is loaded lazily by the graph page, only when the
 * viewer switches to 3D — it must never be imported statically from anything
 * on the ordinary path, or three.js lands in the main bundle. The page's
 * `React.lazy` boundary is the only importer, and GraphPage's test pins that
 * the module is not evaluated until the toggle is used.
 *
 * Same data, same colours and the same focus rule as the 2D canvas: the
 * focused page is a much larger sphere the camera flies to, its links are
 * drawn heavier and in the text colour. Colour strings here are opaque —
 * three.js discards an alpha channel — so the fade is `linkOpacity` instead.
 */
export function GraphCanvas3D({ index, focusId, width, height, onNodeSelect }: GraphCanvasProps) {
  const theme = useTheme()
  const colours = useMemo(() => graphColours(theme), [theme])
  const fgRef = useRef<ForceGraphMethods<Datum, LinkDatum> | undefined>(undefined)

  const graphData = useMemo(
    () => ({
      nodes: index.nodes.map((node) => ({ ...node })) as Datum[],
      links: index.links.map((link) => ({ ...link, source: link.sourceId, target: link.targetId })) as LinkDatum[],
    }),
    [index],
  )
  const neighbourhood = useMemo(() => focusNeighbourhood(index, focusId), [index, focusId])

  const settled = useRef(false)
  useEffect(() => {
    settled.current = false
  }, [graphData])
  const frame = useCallback(() => {
    const fg = fgRef.current
    if (!fg) return
    const focus = focusId === null ? undefined : graphData.nodes.find((node) => node.id === focusId)
    if (focus && focus.x !== undefined && focus.y !== undefined && focus.z !== undefined) {
      // Back the camera off along the line from the origin through the node,
      // looking at the node — the library's own recipe for "fly to".
      const length = Math.hypot(focus.x, focus.y, focus.z) || 1
      const ratio = 1 + FOCUS_DISTANCE / length
      fg.cameraPosition(
        { x: focus.x * ratio, y: focus.y * ratio, z: focus.z * ratio },
        { x: focus.x, y: focus.y, z: focus.z },
        800,
      )
      return
    }
    fg.zoomToFit(600, 40)
  }, [focusId, graphData])
  useEffect(() => {
    if (settled.current) frame()
  }, [frame])

  return (
    <ForceGraph3D<GraphNode, GraphLink>
      ref={fgRef}
      width={width}
      height={height}
      backgroundColor={colours.background}
      showNavInfo={false}
      graphData={graphData}
      nodeId="id"
      nodeVal={(node) => (node.id === focusId ? FOCUS_VAL : 1)}
      nodeColor={(node) => colours.nodeFill(node.marking.level)}
      nodeOpacity={neighbourhood.size > 0 ? 0.85 : 0.95}
      nodeLabel={(node) => nodeTooltip(node)}
      linkColor={(link) => (touchesFocus(link, focusId) ? colours.label : colours.link)}
      linkWidth={(link) => (touchesFocus(link, focusId) ? 1.2 : 0)}
      linkOpacity={0.5}
      linkDirectionalArrowLength={3.5}
      linkDirectionalArrowRelPos={1}
      onNodeClick={(node) => onNodeSelect(node)}
      onEngineStop={() => {
        settled.current = true
        frame()
      }}
    />
  )
}
