import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Chip,
  MenuItem,
  Select,
  Stack,
  TextField,
  Typography,
  type SelectChangeEvent,
} from '@mui/material'
import { DataGrid, GridToolbar, type GridColDef } from '@mui/x-data-grid'
import { useAuditEventsQuery } from '../graphql/generated/graphql'

const OUTCOME_OPTIONS = ['', 'success', 'denied'] as const

/**
 * design.md §7: filter by user/action/subject/outcome/date range, CSV
 * export. Denied events are shown alongside successes, never hidden or
 * defaulted-out — probing restricted content is a signal worth seeing, not
 * noise to filter away by default. Viewing this page is itself audited as
 * `audit.view` — that's a server-side resolver concern (every root field
 * declares its audit action, design.md §7/§8), not something the client
 * needs to trigger separately; the query executing *is* the audited action.
 */
export function AuditLogPage() {
  const [userId, setUserId] = useState('')
  const [action, setAction] = useState('')
  const [subjectType, setSubjectType] = useState('')
  const [outcome, setOutcome] = useState<(typeof OUTCOME_OPTIONS)[number]>('')
  const [fromUtc, setFromUtc] = useState('')
  const [toUtc, setToUtc] = useState('')

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

  const [{ data, fetching, error }] = useAuditEventsQuery({ variables: { filter } })

  const rows = useMemo(() => data?.auditEvents.edges.map((e) => e.node) ?? [], [data])

  const columns: GridColDef[] = [
    { field: 'timestampUtc', headerName: 'Timestamp (UTC)', width: 200 },
    { field: 'userDisplayName', headerName: 'User', width: 160 },
    { field: 'action', headerName: 'Action', width: 160 },
    {
      field: 'subject',
      headerName: 'Subject',
      width: 220,
      valueGetter: (_value, row) =>
        [row.subjectType, row.subjectId, row.subjectSpaceKey && `(${row.subjectSpaceKey})`].filter(Boolean).join(' '),
    },
    {
      field: 'outcome',
      headerName: 'Outcome',
      width: 120,
      renderCell: (params) => (
        <Chip
          label={params.value}
          size="small"
          color={params.value === 'denied' ? 'error' : 'success'}
          variant={params.value === 'denied' ? 'filled' : 'outlined'}
        />
      ),
    },
    { field: 'channel', headerName: 'Channel', width: 110 },
    { field: 'mcpClientName', headerName: 'MCP client', width: 140 },
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
        <TextField
          label="Subject type"
          value={subjectType}
          onChange={(e) => setSubjectType(e.target.value)}
          placeholder="page, space, attachment, …"
          size="small"
          sx={{ minWidth: 180 }}
        />
        <Select
          value={outcome}
          onChange={(e: SelectChangeEvent) => setOutcome(e.target.value as typeof outcome)}
          size="small"
          displayEmpty
          sx={{ minWidth: 140 }}
          aria-label="Outcome"
        >
          <MenuItem value="">All outcomes</MenuItem>
          <MenuItem value="success">Success</MenuItem>
          <MenuItem value="denied">Denied</MenuItem>
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

      {error && (
        <Alert severity="info">Couldn't load audit events — there's no live API in this environment yet.</Alert>
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
    </Stack>
  )
}
