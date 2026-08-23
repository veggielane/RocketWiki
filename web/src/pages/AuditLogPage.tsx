import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Chip,
  MenuItem,
  Select,
  Stack,
  TextField,
  Typography,
  type SelectChangeEvent,
} from '@mui/material'
import { DataGrid, GridToolbar, type GridColDef } from '@mui/x-data-grid'
import {
  useAuditEventsQuery,
  type AuditOutcome,
  type AuditSubjectType,
  type AuditEventsQuery,
} from '../graphql/generated/graphql'

const SUBJECT_TYPE_OPTIONS: AuditSubjectType[] = ['PAGE', 'SPACE', 'ATTACHMENT', 'COMMENT', 'RULE']
const PAGE_SIZE = 100

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
    { field: 'timestampUtc', headerName: 'Timestamp (UTC)', width: 200 },
    { field: 'userDisplayName', headerName: 'User', width: 160 },
    { field: 'action', headerName: 'Action', width: 160 },
    {
      field: 'subject',
      headerName: 'Subject',
      width: 220,
      valueGetter: (_value, row) =>
        [row.subjectType, row.subjectId, row.spaceKey && `(${row.spaceKey})`].filter(Boolean).join(' '),
    },
    {
      field: 'outcome',
      headerName: 'Outcome',
      width: 120,
      renderCell: (params) => (
        <Chip
          label={params.value}
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
      <Typography variant="h4" component="h1">
        Audit log
      </Typography>

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
        <Select
          value={subjectType}
          onChange={(e: SelectChangeEvent) => setSubjectType(e.target.value as AuditSubjectType | '')}
          size="small"
          displayEmpty
          sx={{ minWidth: 170 }}
          aria-label="Subject type"
        >
          <MenuItem value="">All subject types</MenuItem>
          {SUBJECT_TYPE_OPTIONS.map((value) => (
            <MenuItem key={value} value={value}>
              {value.charAt(0) + value.slice(1).toLowerCase()}
            </MenuItem>
          ))}
        </Select>
        <Select
          value={outcome}
          onChange={(e: SelectChangeEvent) => setOutcome(e.target.value as AuditOutcome | '')}
          size="small"
          displayEmpty
          sx={{ minWidth: 140 }}
          aria-label="Outcome"
        >
          <MenuItem value="">All outcomes</MenuItem>
          <MenuItem value="SUCCESS">Success</MenuItem>
          <MenuItem value="DENIED">Denied</MenuItem>
        </Select>
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

      {error && <Alert severity="info">Couldn't load audit events.</Alert>}

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
          slots={{ toolbar: GridToolbar }}
          slotProps={{ toolbar: { showQuickFilter: true } }}
          initialState={{ pagination: { paginationModel: { pageSize: 25 } } }}
          pageSizeOptions={[25, 50, 100]}
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
          Load more
        </Button>
      )}
    </Stack>
  )
}
