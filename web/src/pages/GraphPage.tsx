import { Suspense, lazy, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  Alert,
  Autocomplete,
  Box,
  Button,
  Chip,
  Skeleton,
  Stack,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material'
import HubOutlinedIcon from '@mui/icons-material/HubOutlined'
import TableChartOutlinedIcon from '@mui/icons-material/TableChartOutlined'
import CenterFocusStrongOutlinedIcon from '@mui/icons-material/CenterFocusStrongOutlined'
import { usePageGraphQuery, useSpaceListQuery } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { useSearchParamState } from '../app/useSearchParamState'
import { MarkingLevelBadge } from '../markings/MarkingLevelBadge'
import { GraphCanvas2D } from '../graph/GraphCanvas2D'
import { GraphTable } from '../graph/GraphTable'
import { GRAPH_NODE_LIMIT, indexGraph, markingLegend, type GraphNode } from '../graph/graphModel'
import { useElementSize } from '../graph/useElementSize'
import { pageHref, sameSpaceKey } from './pageSlug'

// The 3D renderer brings three.js with it — the largest dependency in the
// app by a distance — so it is a separate chunk that loads the first time
// someone switches to 3D, and never otherwise. This `lazy` is its only
// importer; a static import anywhere on the ordinary path would put three.js
// in every visitor's download (design.md §12, §15: air-gapped sites, no CDN).
const GraphCanvas3D = lazy(() => import('../graph/GraphCanvas3D').then((m) => ({ default: m.GraphCanvas3D })))

type Dimensions = '2d' | '3d'

/** What the canvas is drawn at before the container has been measured (jsdom, or the first layout pass). */
const FALLBACK_WIDTH = 800
const FALLBACK_HEIGHT = 560

const count = (n: number, noun: string): string => `${n} ${noun}${n === 1 ? '' : 's'}`

/**
 * The document graph (design.md §6.7 / §21.8): every page the caller can
 * view, and which pages link to which.
 *
 * Three things live in the URL — `space`, `focus` and `view` — so a scoped,
 * focused or tabular view can be linked, bookmarked and refreshed
 * (app/useSearchParamState.ts). The scope defaults to the whole instance, by
 * product decision; the space filter narrows it. The 2D/3D choice is local
 * state on purpose: a link that forced three.js onto its recipient would be
 * a heavier link than the person sharing it meant to send.
 *
 * Nothing here filters. The server's answer is already exactly what this
 * caller may see, and a page they may not see is absent — no node, no edge,
 * no placeholder — which is the whole point of the surface. A focused id that
 * is not among the nodes is reported as "not in this graph" in words that do
 * not say which of the possible reasons applies.
 *
 * The canvas is mouse-only by nature. The table view is the same information
 * as a keyboard-navigable, labelled table, and it is a view of its own rather
 * than an afterthought because on a large graph it is the more useful one.
 *
 * On a graph past GRAPH_NODE_LIMIT it is the only one. The simulation is not
 * started at all above that — it would hold the main thread for seconds and
 * draw nothing legible — so the table is shown whatever the address asks
 * for, with a notice saying why and how to narrow the scope. The address is
 * left alone: the canvas comes back on its own once a smaller space is
 * chosen.
 */
export function GraphPage() {
  const [spaceKey, setSpaceKey] = useSearchParamState('space')
  const [focusParam, setFocusParam] = useSearchParamState('focus')
  const [view, setView] = useSearchParamState('view')
  const [dimensions, setDimensions] = useState<Dimensions>('2d')
  const navigate = useNavigate()

  const [{ data: spaceData }] = useSpaceListQuery()
  const spaces = spaceData?.spaces ?? []
  const selectedSpace = spaces.find((space) => sameSpaceKey(space.key, spaceKey))
  useDocumentTitle(selectedSpace ? `Graph — ${selectedSpace.name}` : null)

  // Null, explicitly, for the whole instance: the wire's own spelling of the
  // default scope, rather than a variable quietly left out.
  const [{ data, fetching, error }] = usePageGraphQuery({
    variables: { spaceKey: spaceKey === '' ? null : spaceKey },
  })
  const index = useMemo(() => indexGraph(data?.pageGraph.nodes ?? [], data?.pageGraph.edges ?? []), [data])
  // A property of the node count, not of the toggle: a link to a focused
  // page in a huge instance gets the table too, rather than a frozen tab.
  const tooLarge = index.nodes.length > GRAPH_NODE_LIMIT
  const tableView = view === 'table' || tooLarge
  const focusNode = focusParam === '' ? undefined : index.byId.get(focusParam)
  const focusId = focusNode?.id ?? null
  const legend = useMemo(() => markingLegend(index.nodes), [index])
  const [canvasRef, canvasSize] = useElementSize<HTMLDivElement>()

  const openPage = (node: GraphNode) => navigate(pageHref(node.spaceKey, node.slug, node.id))
  const clearFocus = () => setFocusParam('')

  const scope = selectedSpace ? `in ${selectedSpace.name}` : spaceKey ? `in ${spaceKey}` : 'across the instance'
  const summary = `${count(index.nodes.length, 'page')} and ${count(index.links.length, 'link')} ${scope}`
  const canvasDescription = `Document graph: ${summary}${focusNode ? `, focused on ${focusNode.title}` : ''}. The table view lists the same pages and links.`

  return (
    // Fills the height the shell leaves under its header (AppShell.tsx gives
    // its outlet a definite flexed height for exactly this), so the canvas
    // below can grow into whatever the toolbar, legend and caption leave —
    // `min`, not `height`, so a viewport too short for the floor scrolls
    // rather than squashes.
    <Stack spacing={2} sx={{ minHeight: '100%' }}>
      <PageHeader
        title="Graph"
        description="Every page you can view, and which pages link to which. Click a page to open it."
      />

      <Stack direction="row" spacing={2} useFlexGap sx={{ flexWrap: 'wrap', alignItems: 'flex-start' }}>
        <Autocomplete
          size="small"
          options={spaces.map((space) => space.key)}
          getOptionLabel={(key) => spaces.find((space) => space.key === key)?.name ?? key}
          value={selectedSpace?.key ?? (spaceKey || null)}
          onChange={(_e, value) => setSpaceKey(value ?? '')}
          renderInput={(params) => (
            <TextField {...params} label="Space" helperText="Leave empty for the whole instance." />
          )}
          sx={{ minWidth: 240 }}
        />
        <ToggleButtonGroup
          exclusive
          size="small"
          value={tableView ? 'table' : 'graph'}
          onChange={(_e, next: 'graph' | 'table' | null) => {
            if (next) setView(next === 'table' ? 'table' : '')
          }}
          aria-label="View"
        >
          {/* Disabled, not hidden, above the limit: the control stays where
              it was and the notice below says why it is off. */}
          <ToggleButton value="graph" disabled={tooLarge}>
            <HubOutlinedIcon fontSize="small" sx={{ mr: 0.75 }} aria-hidden />
            Graph
          </ToggleButton>
          <ToggleButton value="table">
            <TableChartOutlinedIcon fontSize="small" sx={{ mr: 0.75 }} aria-hidden />
            Table
          </ToggleButton>
        </ToggleButtonGroup>
        {!tableView && (
          <ToggleButtonGroup
            exclusive
            size="small"
            value={dimensions}
            onChange={(_e, next: Dimensions | null) => {
              if (next) setDimensions(next)
            }}
            aria-label="Dimensions"
          >
            <ToggleButton value="2d">2D</ToggleButton>
            <ToggleButton value="3d">3D</ToggleButton>
          </ToggleButtonGroup>
        )}
        {focusNode && (
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <Chip icon={<CenterFocusStrongOutlinedIcon />} label={`Focused: ${focusNode.title}`} variant="outlined" />
            <Button size="small" onClick={clearFocus}>
              Clear focus
            </Button>
          </Stack>
        )}
      </Stack>

      {fetching && !data ? (
        <Skeleton variant="rectangular" height={420} />
      ) : error ? (
        <Alert severity="info">{describeLoadFailure('PAGE_GRAPH').summary}</Alert>
      ) : (
        <>
          {/* Deliberately one sentence for every empty answer: a space the
              caller cannot enter, an archived one and a key naming nothing all
              come back as the same empty graph, and the screen must not
              sharpen that into any one of them (design.md §21.8). */}
          {index.nodes.length === 0 ? (
            <Stack spacing={1} sx={{ alignItems: 'flex-start' }}>
              <Typography color="text.secondary">
                Nothing to draw — there are no pages you can view {scope}.
              </Typography>
              {spaceKey !== '' && (
                <Button size="small" onClick={() => setSpaceKey('')}>
                  Show the whole instance
                </Button>
              )}
            </Stack>
          ) : (
            <>
              {tooLarge && (
                <Alert severity="info">
                  The graph is too large to draw — {count(index.nodes.length, 'page')}, and the canvas stops at{' '}
                  {GRAPH_NODE_LIMIT}. Showing the table instead; choose a space to narrow it, or use the filter to
                  find a page.
                </Alert>
              )}
              {focusParam !== '' && !focusNode && (
                <Alert
                  severity="info"
                  action={
                    <Button color="inherit" size="small" onClick={clearFocus}>
                      Clear focus
                    </Button>
                  }
                >
                  The focused page isn't in this graph — it may be outside the selected space, or not something you
                  can view here.
                </Alert>
              )}

              <Typography variant="body2" color="text.secondary">
                {summary}.
              </Typography>

              {tableView ? (
                <GraphTable index={index} focusId={focusId} />
              ) : (
                <>
                  <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', alignItems: 'center' }}>
                    <Typography variant="caption" color="text.secondary">
                      Node colour is the page's classification:
                    </Typography>
                    {legend.map((entry) => (
                      <MarkingLevelBadge key={entry.level} level={entry.level} levelName={entry.levelName} />
                    ))}
                  </Stack>
                  {/* role="img" with a description, rather than nothing: the
                      canvas has no DOM a screen reader could walk, so this is
                      what it is told — what the picture shows, and where the
                      same information is reachable. */}
                  {/* Sized by the flex layout alone, never by what is drawn in
                      it: `flexBasis: 0` with `overflow: hidden` means the
                      canvas element force-graph puts inside — whose pixel size
                      is set from the measurement of THIS box — cannot feed back
                      into the box's own size. With `flex-basis: auto` it
                      would: the first measurement would become the content
                      height, the content height the box's minimum, and the box
                      could never shrink below whatever it was first drawn at.
                      The floor is what a short window falls back to, at which
                      point the page scrolls (see the root Stack). */}
                  <Box
                    ref={canvasRef}
                    role="img"
                    aria-label={canvasDescription}
                    sx={{
                      flexGrow: 1,
                      flexBasis: 0,
                      minHeight: 320,
                      border: 1,
                      borderColor: 'divider',
                      borderRadius: 1,
                      overflow: 'hidden',
                      bgcolor: 'background.default',
                    }}
                  >
                    {dimensions === '3d' ? (
                      <Suspense fallback={<Skeleton variant="rectangular" height="100%" />}>
                        <GraphCanvas3D
                          index={index}
                          focusId={focusId}
                          width={canvasSize.width || FALLBACK_WIDTH}
                          height={canvasSize.height || FALLBACK_HEIGHT}
                          onNodeSelect={openPage}
                        />
                      </Suspense>
                    ) : (
                      <GraphCanvas2D
                        index={index}
                        focusId={focusId}
                        width={canvasSize.width || FALLBACK_WIDTH}
                        height={canvasSize.height || FALLBACK_HEIGHT}
                        onNodeSelect={openPage}
                      />
                    )}
                  </Box>
                  <Typography variant="caption" color="text.secondary">
                    The graph is drawn on a canvas and needs a pointer. The table view lists the same pages and
                    links for keyboard and screen-reader use.
                  </Typography>
                </>
              )}
            </>
          )}
        </>
      )}
    </Stack>
  )
}
