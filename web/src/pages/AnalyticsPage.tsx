import { useMemo, useState } from 'react'
import { Link as RouterLink, useParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Divider,
  Link,
  MenuItem,
  Paper,
  Skeleton,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { useAnalyticsQuery } from '../graphql/generated/graphql'
import { ActivityChart } from '../analytics/ActivityChart'

/** Windows offered, in days. Kept short and round — a date-range picker invites
 * precision this data does not have, and every option here is one query. */
const PERIODS = [
  { days: 7, label: 'Last 7 days' },
  { days: 30, label: 'Last 30 days' },
  { days: 90, label: 'Last 90 days' },
]

/**
 * Usage analytics for one space, or the whole instance when the route carries no
 * space key (design.md §7).
 *
 * Not router-gated. The server returns null for a caller who administers neither
 * the instance nor this space, and for a space key that does not resolve — the
 * same "absent, not forbidden" convention every other read follows (§6.7), so a
 * guard here would only duplicate a decision the server has already made and can
 * enforce.
 *
 * **Every number is scoped to what this caller can see.** Two admins with
 * different clearances legitimately see different totals, which is why the header
 * states the page count the report covered rather than leaving the reader to
 * assume it is the whole space.
 */
export function AnalyticsPage() {
  const { spaceKey } = useParams<{ spaceKey?: string }>()
  const [days, setDays] = useState(30)

  // Recomputed only when the window changes, not on every render: a moving `toUtc`
  // would make the query variables new each time and refetch forever.
  const { fromUtc, toUtc } = useMemo(() => {
    const end = new Date()
    end.setUTCHours(0, 0, 0, 0)
    end.setUTCDate(end.getUTCDate() + 1)
    const start = new Date(end)
    start.setUTCDate(start.getUTCDate() - days)
    return { fromUtc: start.toISOString(), toUtc: end.toISOString() }
  }, [days])

  const [{ data, fetching, error }] = useAnalyticsQuery({
    variables: { spaceKey: spaceKey ?? null, fromUtc, toUtc },
  })

  if (fetching) {
    return <Skeleton variant="rectangular" height={420} />
  }

  const report = data?.analytics
  if (error || !report) {
    return (
      <Alert severity="info">
        {spaceKey
          ? 'Analytics for this space are available to its space admins and to instance admins.'
          : 'Site analytics are available to instance admins.'}
      </Alert>
    )
  }

  const totalViews = report.activity.reduce((n, point) => n + point.views, 0)
  const totalEdits = report.activity.reduce((n, point) => n + point.edits, 0)

  return (
    <Stack spacing={3}>
      <Stack direction="row" spacing={2} alignItems="flex-start" justifyContent="space-between" flexWrap="wrap">
        {/* flexGrow on the title, not just space-between: with both children
            sized to their content the selector sat mid-header rather than at the
            right edge. */}
        <Stack spacing={0.5} sx={{ flexGrow: 1, minWidth: 240 }}>
          <Typography variant="h4" component="h1">
            {spaceKey ? `Analytics: ${spaceKey}` : 'Site analytics'}
          </Typography>
          {/* Says what the numbers are OF. A report filtered by clearance that
              presented itself as the whole space would be quietly misleading. */}
          <Typography variant="body2" color="text.secondary">
            Counted over the {report.scope.visiblePageCount} page
            {report.scope.visiblePageCount === 1 ? '' : 's'} you can view.
          </Typography>
        </Stack>
        <TextField
          select
          size="small"
          label="Period"
          value={days}
          onChange={(e) => setDays(Number(e.target.value))}
          sx={{ minWidth: 160 }}
        >
          {PERIODS.map((period) => (
            <MenuItem key={period.days} value={period.days}>
              {period.label}
            </MenuItem>
          ))}
        </TextField>
      </Stack>

      <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap>
        <StatTile label="Views" value={totalViews} />
        <StatTile label="Edits" value={totalEdits} />
        <StatTile label="Readers" value={report.topReaders.length} hint="in the top ten" />
        <StatTile label="Pages" value={report.scope.visiblePageCount} hint="visible to you" />
      </Stack>

      <Panel title="Activity">
        <ActivityChart data={report.activity.map((p) => ({ day: p.day as string, views: p.views, edits: p.edits }))} />
      </Panel>

      <Stack direction={{ xs: 'column', md: 'row' }} spacing={3} alignItems="stretch">
        <Panel title="Most viewed" grow>
          <PageList rows={report.mostViewed} unit="views" />
        </Panel>
        <Panel title="Most edited" grow>
          <PageList rows={report.mostEdited} unit="edits" />
        </Panel>
      </Stack>

      <Stack direction={{ xs: 'column', md: 'row' }} spacing={3} alignItems="stretch">
        <Panel title="Top readers" grow>
          {/* Named at the instance owner's direction. Reading this page is itself
              an audited event — see Query.Analytics. */}
          <PersonList rows={report.topReaders} unit="views" />
        </Panel>
        <Panel title="Top contributors" grow>
          <PersonList rows={report.topContributors} unit="edits" />
        </Panel>
      </Stack>

      <Panel title="Content health">
        <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap sx={{ mb: 2 }}>
          <StatTile label="Stale" value={report.health.stalePageCount} hint="no edit in 6 months" />
          <StatTile label="Top level" value={report.health.orphanPageCount} hint="no parent page" />
          <StatTile label="Unlabelled" value={report.health.unlabelledPageCount} />
          <StatTile label="Never viewed" value={report.health.neverViewedPageCount} hint="in this period" />
        </Stack>
        {report.health.stalestPages.length > 0 && (
          <>
            <Divider sx={{ mb: 1.5 }} />
            <Typography variant="subtitle2" gutterBottom>
              Longest untouched
            </Typography>
            <PageList rows={report.health.stalestPages} unit="days" />
          </>
        )}
      </Panel>

      <Stack direction={{ xs: 'column', md: 'row' }} spacing={3} alignItems="stretch">
        <Panel title="Top searches" grow>
          <SearchList rows={report.topSearches} />
        </Panel>
        <Panel title="Searches that found nothing" grow>
          <SearchList rows={report.zeroResultSearches} empty="Every search in this period returned something." />
        </Panel>
      </Stack>
    </Stack>
  )
}

function Panel({ title, children, grow = false }: { title: string; children: React.ReactNode; grow?: boolean }) {
  return (
    <Paper variant="outlined" sx={{ p: 2, flex: grow ? 1 : undefined, minWidth: 0 }}>
      <Typography variant="h6" component="h2" gutterBottom>
        {title}
      </Typography>
      {children}
    </Paper>
  )
}

/**
 * A hero number with its label. Deliberately not a chart: a single magnitude with
 * nothing to compare against is a number, and drawing it as a one-bar chart adds
 * axes and ink that carry no information.
 */
function StatTile({ label, value, hint }: { label: string; value: number; hint?: string }) {
  return (
    // <figure>/<figcaption>, not two loose <p>s: it makes the number and its label
    // one thing with one accessible name, so a screen reader reads "Views, 13"
    // rather than a bare number floating next to a word.
    <Paper
      variant="outlined"
      component="figure"
      aria-label={label}
      sx={{ px: 2, py: 1.5, minWidth: 140, m: 0 }}
    >
      <Typography variant="h4" component="p">
        {value.toLocaleString()}
      </Typography>
      <Box component="figcaption">
        <Typography variant="body2" color="text.secondary">
          {label}
        </Typography>
        {hint && (
          <Typography variant="caption" color="text.secondary">
            {hint}
          </Typography>
        )}
      </Box>
    </Paper>
  )
}

function EmptyRow({ children }: { children: React.ReactNode }) {
  return (
    <Typography variant="body2" color="text.secondary">
      {children}
    </Typography>
  )
}

function PageList({
  rows,
  unit,
}: {
  rows: readonly { pageId: string; title: string; slug: string; spaceKey: string; count: number }[]
  unit: string
}) {
  if (rows.length === 0) return <EmptyRow>Nothing in this period.</EmptyRow>
  return (
    <Stack component="ol" sx={{ listStyle: 'none', m: 0, p: 0 }} spacing={0.75}>
      {rows.map((row) => (
        <Stack key={row.pageId} component="li" direction="row" justifyContent="space-between" spacing={2}>
          {/* The readable address, so a row is somewhere you can go rather than a
              name you have to search for. */}
          <Link component={RouterLink} to={`/spaces/${row.spaceKey}/${row.slug}`} noWrap sx={{ minWidth: 0 }}>
            {row.title}
          </Link>
          <Typography variant="body2" color="text.secondary" whiteSpace="nowrap">
            {row.count.toLocaleString()} {unit}
          </Typography>
        </Stack>
      ))}
    </Stack>
  )
}

function PersonList({ rows, unit }: { rows: readonly { userId: string; displayName: string; count: number }[]; unit: string }) {
  if (rows.length === 0) return <EmptyRow>Nobody in this period.</EmptyRow>
  return (
    <Stack component="ol" sx={{ listStyle: 'none', m: 0, p: 0 }} spacing={0.75}>
      {rows.map((row) => (
        <Stack key={row.userId} component="li" direction="row" justifyContent="space-between" spacing={2}>
          <Typography variant="body2" noWrap sx={{ minWidth: 0 }}>
            {row.displayName}
          </Typography>
          <Typography variant="body2" color="text.secondary" whiteSpace="nowrap">
            {row.count.toLocaleString()} {unit}
          </Typography>
        </Stack>
      ))}
    </Stack>
  )
}

function SearchList({
  rows,
  empty = 'Nothing in this period.',
}: {
  rows: readonly { query: string; runCount: number; zeroResultCount: number }[]
  empty?: string
}) {
  if (rows.length === 0) return <EmptyRow>{empty}</EmptyRow>
  return (
    <Stack component="ol" sx={{ listStyle: 'none', m: 0, p: 0 }} spacing={0.75}>
      {rows.map((row) => (
        <Stack key={row.query} component="li" direction="row" justifyContent="space-between" spacing={2}>
          <Box component="span" sx={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            <Typography variant="body2" component="span">
              {row.query}
            </Typography>
          </Box>
          <Typography variant="body2" color="text.secondary" whiteSpace="nowrap">
            {row.runCount.toLocaleString()}
          </Typography>
        </Stack>
      ))}
    </Stack>
  )
}
