import { useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Alert, Box, Button, Chip, MenuItem, Paper, Stack, TextField, Typography } from '@mui/material'
import RefreshIcon from '@mui/icons-material/Refresh'
import { DataGrid, type GridColDef } from '@mui/x-data-grid'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { useDebouncedSearchParam, useSearchParamState } from '../app/useSearchParamState'
import {
  useAuditEventsQuery,
  type AuditOutcome,
  type AuditSubjectType,
  type AuditEventsQuery,
} from '../graphql/generated/graphql'

const SUBJECT_TYPE_OPTIONS: AuditSubjectType[] = ['PAGE', 'SPACE', 'ATTACHMENT', 'COMMENT', 'RULE']

/**
 * The written form of each wire value, used by BOTH the filter control and the
 * grid cell. They were spelled differently — the filter offered "Page" and
 * "Denied" while the rows showed `PAGE` and `DENIED`, eighty pixels apart on
 * one screen.
 */
const SUBJECT_TYPE_LABELS: Record<AuditSubjectType, string> = {
  PAGE: 'Page',
  SPACE: 'Space',
  ATTACHMENT: 'Attachment',
  COMMENT: 'Comment',
  RULE: 'Rule',
}

const OUTCOME_LABELS: Record<AuditOutcome, string> = {
  SUCCESS: 'Success',
  DENIED: 'Denied',
}

/** One server page. "Load more" appends the next; see the note on the grid below. */
const PAGE_SIZE = 100

/** Long enough that a typed word is one request, short enough to feel live. */
const FILTER_DEBOUNCE_MS = 300

/** Every filter this screen owns, so "clear all" and the URL cannot drift apart. */
const FILTER_PARAMS = ['user', 'action', 'subject', 'outcome', 'from', 'to'] as const

/** `2026-08-29 09:14:22` — sortable, scannable, and unambiguously the stored UTC instant. */
function formatUtcTimestamp(value: string | null | undefined): string {
  if (!value) return ''
  const parsed = new Date(value)
  if (Number.isNaN(parsed.getTime())) return value
  return parsed.toISOString().replace('T', ' ').replace(/\.\d+Z$/, '')
}

type AuditRow = NonNullable<NonNullable<AuditEventsQuery['auditEvents']>['nodes']>[number]

/**
 * design.md §7: filter by user/action/subject/outcome/date range, CSV
 * export (DataGrid's toolbar). Denied events are shown alongside
 * successes, never hidden or defaulted-out — probing restricted content is
 * a signal worth seeing, not noise to filter away by default. Viewing this
 * page is itself audited as `audit.view` server-side; the query executing
 * *is* the audited action. The connection is cursor-paged with a server
 * `totalCount` for the matching-event total; "Load more" appends the next
 * page.
 *
 * **The filters live in the URL.** Deep-linking a filtered view is the thing a
 * compliance reviewer does — "every denied event on SPACE last week" is a link
 * they send to someone, not a sequence of six controls they describe.
 */
export function AuditLogPage() {
  useDocumentTitle('Audit log')
  const [, setParams] = useSearchParams()
  const [userDraft, setUserDraft, userId] = useDebouncedSearchParam('user', FILTER_DEBOUNCE_MS)
  const [actionDraft, setActionDraft, action] = useDebouncedSearchParam('action', FILTER_DEBOUNCE_MS)
  const [subjectType, setSubjectType] = useSearchParamState('subject')
  const [outcome, setOutcome] = useSearchParamState('outcome')
  const [fromUtc, setFromUtc] = useSearchParamState('from')
  const [toUtc, setToUtc] = useSearchParamState('to')
  const [after, setAfter] = useState<string | null>(null)
  const [loadedRows, setLoadedRows] = useState<AuditRow[]>([])

  const filter = useMemo(
    () => ({
      userId: userId || null,
      action: action || null,
      subjectType: (subjectType || null) as AuditSubjectType | null,
      outcome: (outcome || null) as AuditOutcome | null,
      fromUtc: fromUtc ? new Date(fromUtc).toISOString() : null,
      toUtc: toUtc ? new Date(toUtc).toISOString() : null,
    }),
    [userId, action, subjectType, outcome, fromUtc, toUtc],
  )

  const hasFilters = userId !== '' || action !== '' || subjectType !== '' || outcome !== '' || fromUtc !== '' || toUtc !== ''

  const clearFilters = () =>
    setParams(
      (current) => {
        const next = new URLSearchParams(current)
        for (const name of FILTER_PARAMS) next.delete(name)
        return next
      },
      { replace: true },
    )

  // A changed filter restarts pagination (`after` reset by the setters
  // below via key comparison): accumulated rows belong to the old filter.
  const filterKey = JSON.stringify(filter)
  const [loadedForFilter, setLoadedForFilter] = useState(filterKey)
  if (loadedForFilter !== filterKey) {
    setLoadedForFilter(filterKey)
    setAfter(null)
    setLoadedRows([])
  }

  const [{ data, fetching, error }, refetch] = useAuditEventsQuery({
    variables: { filter, first: PAGE_SIZE, after },
  })

  const rows = useMemo(() => {
    const fresh = data?.auditEvents?.nodes ?? []
    const seen = new Set(loadedRows.map((r) => r.id))
    return [...loadedRows, ...fresh.filter((r) => !seen.has(r.id))]
  }, [data, loadedRows])

  const pageInfo = data?.auditEvents?.pageInfo
  const totalCount = data?.auditEvents?.totalCount

  const refresh = () => {
    // Back to the first server page: "refresh" means the newest events, and
    // appending them to a stale accumulation would interleave two points in time.
    setAfter(null)
    setLoadedRows([])
    refetch({ requestPolicy: 'network-only' })
  }

  const columns: GridColDef[] = ([
    {
      field: 'timestampUtc',
      headerName: 'Timestamp (UTC)',
      width: 200,
      // Formatted, not raw. The primary column of the audit log used to render
      // the wire value — `2026-08-29T09:14:22.481Z` — in a table people read by
      // scanning down it. UTC on purpose (the header says so): an auditor
      // correlating with server logs must not be shown a local-time rendering
      // that silently differs from the record.
      valueFormatter: (value: string | null) => formatUtcTimestamp(value),
    },
    { field: 'userDisplayName', headerName: 'User', width: 160 },
    { field: 'action', headerName: 'Action', width: 160 },
    {
      field: 'subject',
      headerName: 'Subject',
      width: 260,
      // Title-cased through the same map the subject-type filter uses, so one
      // screen does not spell the same value `PAGE` in a cell and "Page" in the
      // control that filters on it.
      valueGetter: (_value, row) =>
        [SUBJECT_TYPE_LABELS[row.subjectType as AuditSubjectType] ?? row.subjectType, row.subjectId, row.spaceKey && `(${row.spaceKey})`]
          .filter(Boolean)
          .join(' '),
    },
    {
      field: 'outcome',
      headerName: 'Outcome',
      width: 120,
      renderCell: (params) => (
        <Chip
          label={OUTCOME_LABELS[params.value as AuditOutcome] ?? params.value}
          size="small"
          color={params.value === 'DENIED' ? 'error' : 'success'}
          variant={params.value === 'DENIED' ? 'filled' : 'outlined'}
        />
      ),
    },
    { field: 'channel', headerName: 'Channel', width: 110 },
    { field: 'mcpClient', headerName: 'MCP client', width: 140 },
    // Not sortable, deliberately, and this is why the whole column list says so
    // once rather than each entry repeating it: `rows` is what has been fetched
    // so far, not the whole result set. A header click would reorder the loaded
    // slice and present it as "oldest first" — an answer about 100 rows dressed
    // up as an answer about the log. Server-side ordering is newest-first; the
    // date range is how you ask for a different window.
  ] satisfies GridColDef[]).map((column) => ({ ...column, sortable: false }))

  return (
    // No `height: '100%'` here. It was dead when written — the shell's outlet
    // had no height to resolve it against — and the outlet now has one
    // (AppShell.tsx), so it would have started pinning this Stack to the
    // region's height, growing the grid into the leftover on a tall window and
    // spilling the Load-more button past the banner reservation on a short
    // one. Filling the region is a decision for this screen to make on
    // purpose, with the grid told how to behave when it does.
    <Stack spacing={2}>
      <PageHeader
        title="Audit log"
        description="Every recorded action, newest first, with times shown in UTC to match the stored record. Refused attempts appear alongside successful ones. Filters are part of the address, so a filtered view can be bookmarked or sent to someone."
        actions={
          <Button startIcon={<RefreshIcon />} onClick={refresh} disabled={fetching}>
            Refresh
          </Button>
        }
      />

      {/* In a Paper, like the forms on the sibling admin screens — six controls
          floating directly on the page read as part of the table's header. */}
      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack direction="row" spacing={2} useFlexGap sx={{ flexWrap: 'wrap', alignItems: 'flex-start' }}>
          <TextField
            label="User ID"
            value={userDraft}
            onChange={(e) => setUserDraft(e.target.value)}
            size="small"
            sx={{ minWidth: 180 }}
          />
          <TextField
            label="Action"
            value={actionDraft}
            onChange={(e) => setActionDraft(e.target.value)}
            placeholder="page.view, page.edit, …"
            size="small"
            sx={{ minWidth: 200 }}
          />
          {/* `TextField select`, not a bare `Select` with an `aria-label`: these
              two sat unlabelled among four labelled fields, so a sighted user
              could not tell what the box selected without opening it. The
              floating label is what every neighbour already has. */}
          <TextField
            select
            label="Subject type"
            value={subjectType}
            onChange={(e) => setSubjectType(e.target.value)}
            size="small"
            sx={{ minWidth: 170 }}
          >
            <MenuItem value="">All subject types</MenuItem>
            {SUBJECT_TYPE_OPTIONS.map((value) => (
              <MenuItem key={value} value={value}>
                {SUBJECT_TYPE_LABELS[value]}
              </MenuItem>
            ))}
          </TextField>
          <TextField
            select
            label="Outcome"
            value={outcome}
            onChange={(e) => setOutcome(e.target.value)}
            size="small"
            sx={{ minWidth: 140 }}
          >
            <MenuItem value="">All outcomes</MenuItem>
            <MenuItem value="SUCCESS">{OUTCOME_LABELS.SUCCESS}</MenuItem>
            <MenuItem value="DENIED">{OUTCOME_LABELS.DENIED}</MenuItem>
          </TextField>
          <TextField
            label="From"
            type="date"
            value={fromUtc}
            onChange={(e) => setFromUtc(e.target.value)}
            size="small"
            slotProps={{ inputLabel: { shrink: true } }}
          />
          <TextField
            label="To"
            type="date"
            value={toUtc}
            onChange={(e) => setToUtc(e.target.value)}
            size="small"
            slotProps={{ inputLabel: { shrink: true } }}
          />
          {hasFilters && (
            <Button onClick={clearFilters} sx={{ mt: 0.5 }}>
              Clear filters
            </Button>
          )}
        </Stack>
      </Paper>

      {error && <Alert severity="info">{describeLoadFailure('AUDIT_EVENTS').summary}</Alert>}

      {!error && totalCount !== undefined && (
        <Typography variant="body2" color="text.secondary">
          {totalCount.toLocaleString()} matching event{totalCount === 1 ? '' : 's'}
          {rows.length < totalCount ? ` — showing ${rows.length.toLocaleString()}` : ''}
        </Typography>
      )}

      {/* An empty result is a fact with a consequence, said in a sentence — the
          treatment the registry screens already use. The grid's built-in "No
          rows" overlay says only that the table is empty, and rendering it under
          an error Alert claimed a successful, empty query. */}
      {!error && !fetching && rows.length === 0 && (
        <Typography color="text.secondary">
          {hasFilters
            ? 'No recorded events match these filters. Widen the date range or clear a filter — an event only appears here after it happened.'
            : 'No events have been recorded yet. Actions are written to the log as they happen; this page will fill in on its own.'}
        </Typography>
      )}

      {!error && (
        <Box sx={{ flexGrow: 1, minHeight: 400 }}>
          <DataGrid
            aria-label="Audit events"
            rows={rows}
            columns={columns}
            getRowId={(row) => row.id}
            loading={fetching}
            // Compact, like the registries. This is the longest table in the app;
            // it was the only one paying 52px a row for the privilege.
            density="compact"
            // `showToolbar`, not `slots={{ toolbar: GridToolbar }}` — GridToolbar
            // is deprecated in the installed MUI X 9 ("use the showToolbar prop")
            // and is slated for removal. The default toolbar still carries the
            // CSV export design.md §7 asks for.
            showToolbar
            // …but not the grid's own filter panel. Six server-side filter fields
            // sit directly above, and a second set of identical-looking controls
            // filtering only the fetched slice is two answers to one question.
            disableColumnFilter
            // One paginator, not two. The grid used to page client-side at 25 over
            // the accumulated rows WHILE a separate "Load more" fetched the next
            // 100 from the server — so a user on grid page 1 of 4 who pressed
            // Load more saw nothing change, because the new rows landed on pages
            // 5-8 of a paginator they were not looking at. The grid now shows
            // everything that has been loaded and scrolls; fetching more is the
            // button's job alone.
            hideFooterPagination
            disableRowSelectionOnClick
          />
        </Box>
      )}

      {!error && pageInfo?.hasNextPage && (
        <Button
          variant="outlined"
          size="small"
          disabled={fetching}
          onClick={() => {
            setLoadedRows(rows)
            setAfter(pageInfo.endCursor ?? null)
          }}
          sx={{ alignSelf: 'flex-start' }}
        >
          {fetching ? 'Loading…' : `Load ${PAGE_SIZE} more`}
        </Button>
      )}
    </Stack>
  )
}
