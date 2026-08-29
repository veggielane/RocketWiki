import { useMemo, useState } from 'react'
import { Alert, Box, Button, Chip, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { DataGrid, type GridColDef } from '@mui/x-data-grid'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
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

/** One server page. The grid pages over what has been loaded; see the note by `paginationModel`. */
const PAGE_SIZE = 100

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
 */
export function AuditLogPage() {
  useDocumentTitle('Audit log')
  const [userId, setUserId] = useState('')
  const [action, setAction] = useState('')
  const [subjectType, setSubjectType] = useState<AuditSubjectType | ''>('')
  const [outcome, setOutcome] = useState<AuditOutcome | ''>('')
  const [fromUtc, setFromUtc] = useState('')
  const [toUtc, setToUtc] = useState('')
  const [after, setAfter] = useState<string | null>(null)
  const [loadedRows, setLoadedRows] = useState<AuditRow[]>([])

  const filter = useMemo(
    () => ({
      userId: userId || null,
      action: action || null,
      subjectType: subjectType || null,
      outcome: outcome || null,
      fromUtc: fromUtc ? new Date(fromUtc).toISOString() : null,
      toUtc: toUtc ? new Date(toUtc).toISOString() : null,
    }),
    [userId, action, subjectType, outcome, fromUtc, toUtc],
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

  const [{ data, fetching, error }] = useAuditEventsQuery({
    variables: { filter, first: PAGE_SIZE, after },
  })

  const rows = useMemo(() => {
    const fresh = data?.auditEvents?.nodes ?? []
    const seen = new Set(loadedRows.map((r) => r.id))
    return [...loadedRows, ...fresh.filter((r) => !seen.has(r.id))]
  }, [data, loadedRows])

  const pageInfo = data?.auditEvents?.pageInfo
  const totalCount = data?.auditEvents?.totalCount

  const columns: GridColDef[] = [
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
  ]

  return (
    <Stack spacing={2} sx={{ height: '100%' }}>
      <PageHeader title="Audit log" />

      <Stack direction="row" spacing={2} useFlexGap sx={{ flexWrap: 'wrap' }}>
        <TextField
          label="User ID"
          value={userId}
          onChange={(e) => setUserId(e.target.value)}
          size="small"
          sx={{ minWidth: 180 }}
        />
        <TextField
          label="Action"
          value={action}
          onChange={(e) => setAction(e.target.value)}
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
          onChange={(e) => setSubjectType(e.target.value as AuditSubjectType | '')}
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
          onChange={(e) => setOutcome(e.target.value as AuditOutcome | '')}
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
      </Stack>

      {error && <Alert severity="info">{describeLoadFailure('AUDIT_EVENTS').summary}</Alert>}

      {totalCount !== undefined && (
        <Typography variant="body2" color="text.secondary">
          {totalCount.toLocaleString()} matching event{totalCount === 1 ? '' : 's'}
          {rows.length < totalCount ? ` — showing ${rows.length.toLocaleString()}` : ''}
        </Typography>
      )}

      <Box sx={{ flexGrow: 1, minHeight: 400 }}>
        <DataGrid
          aria-label="Audit events"
          rows={rows}
          columns={columns}
          getRowId={(row) => row.id}
          loading={fetching}
          // `showToolbar`, not `slots={{ toolbar: GridToolbar }}` — GridToolbar
          // is deprecated in the installed MUI X 9 ("use the showToolbar prop")
          // and is slated for removal. The default toolbar still carries the
          // CSV export design.md §7 asks for.
          showToolbar
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

      {pageInfo?.hasNextPage && (
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
