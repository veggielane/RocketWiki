import { useMemo, useState } from 'react'
import { Link as RouterLink } from 'react-router-dom'
import {
  Box,
  Link,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material'
import CenterFocusStrongOutlinedIcon from '@mui/icons-material/CenterFocusStrongOutlined'
import { MarkingLevelBadge } from '../markings/MarkingLevelBadge'
import { lookupPageIcon } from '../pages/pageIcons'
import { pageHref } from '../pages/pageSlug'
import { GRAPH_NODE_LIMIT, filterRows, graphRows, type GraphIndex, type GraphNode } from './graphModel'

const pages = (count: number): string => `${count} ${count === 1 ? 'page' : 'pages'}`

/** The linked pages in one cell: an inline list of links, or "None". */
function LinkList({ pages: linked, describe }: { pages: readonly GraphNode[]; describe: string }) {
  if (linked.length === 0) {
    return (
      <Typography variant="body2" color="text.secondary">
        None
      </Typography>
    )
  }
  return (
    <Box component="ul" aria-label={`${describe} ${pages(linked.length)}`} sx={{ m: 0, pl: 0, listStyle: 'none' }}>
      {linked.map((page) => (
        <li key={page.id}>
          {/* One link per line, each a 2.5.8-sized target: an inline,
              comma-separated run wrapped into 19px lines whose neighbours sat
              directly above and below, and the browser tier flagged every one. */}
          <Link
            component={RouterLink}
            to={pageHref(page.spaceKey, page.slug, page.id)}
            variant="body2"
            sx={{ display: 'inline-block', py: 0.5 }}
          >
            {page.title}
          </Link>
        </li>
      ))}
    </Box>
  )
}

/**
 * The accessible equivalent of the canvas — and the useful one on a large
 * graph: every page the caller can view in scope, what each links to and
 * what links to it, as a real table of real links. Keyboard-navigable and
 * labelled throughout, because the canvas beside it is neither, and a
 * force-directed drawing must never be the only way to learn what links to
 * what.
 *
 * The focused page is marked in its row by a labelled icon and
 * `aria-current`, on top of the selected-row background, so the highlight
 * survives a monochrome screen and reaches a screen reader.
 */
export function GraphTable({ index, focusId }: { index: GraphIndex; focusId: string | null }) {
  const [filter, setFilter] = useState('')
  const rows = useMemo(() => graphRows(index), [index])
  const matching = useMemo(() => filterRows(rows, filter), [rows, filter])
  const shown = matching.slice(0, GRAPH_NODE_LIMIT)

  return (
    <Stack spacing={1.5}>
      <TextField
        size="small"
        label="Filter pages"
        value={filter}
        onChange={(e) => setFilter(e.target.value)}
        helperText="Matches a page's title or its space key."
        sx={{ maxWidth: 360 }}
      />
      <Typography variant="body2" color="text.secondary" role="status">
        {matching.length === rows.length
          ? `${pages(rows.length)}.`
          : `${matching.length} of ${pages(rows.length)} match.`}
        {shown.length < matching.length && ` Showing the first ${shown.length} — narrow the filter to reach the rest.`}
      </Typography>
      {shown.length > 0 && (
        <TableContainer component={Paper} variant="outlined">
          <Table size="small" aria-label="Pages and their links">
            <TableHead>
              <TableRow>
                <TableCell>Page</TableCell>
                <TableCell>Space</TableCell>
                <TableCell>Classification</TableCell>
                <TableCell>Links to</TableCell>
                <TableCell>Linked from</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {shown.map((row) => {
                const focused = row.node.id === focusId
                const Icon = lookupPageIcon(row.node.icon)?.Icon
                return (
                  <TableRow key={row.node.id} selected={focused} aria-current={focused ? 'true' : undefined}>
                    <TableCell component="th" scope="row" sx={{ verticalAlign: 'top' }}>
                      <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center' }}>
                        {focused && <CenterFocusStrongOutlinedIcon fontSize="small" titleAccess="Focused page" />}
                        {/* Decorative: the title beside it names the page. */}
                        {Icon && <Icon fontSize="small" sx={{ color: 'text.secondary' }} aria-hidden />}
                        <Link
                          component={RouterLink}
                          to={pageHref(row.node.spaceKey, row.node.slug, row.node.id)}
                          sx={{ fontWeight: focused ? 700 : 500 }}
                        >
                          {row.node.title}
                        </Link>
                      </Stack>
                    </TableCell>
                    <TableCell sx={{ verticalAlign: 'top', whiteSpace: 'nowrap' }}>{row.node.spaceKey}</TableCell>
                    <TableCell sx={{ verticalAlign: 'top' }}>
                      <MarkingLevelBadge level={row.node.marking.level} levelName={row.node.marking.levelName} />
                    </TableCell>
                    <TableCell sx={{ verticalAlign: 'top' }}>
                      <LinkList pages={row.linksTo} describe="Links to" />
                    </TableCell>
                    <TableCell sx={{ verticalAlign: 'top' }}>
                      <LinkList pages={row.linkedFrom} describe="Linked from" />
                    </TableCell>
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </Stack>
  )
}
