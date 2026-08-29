import { Alert, Chip, Paper, Stack, Typography } from '@mui/material'
import { type GridColDef } from '@mui/x-data-grid'
import { useSyncStatusQuery, type SyncStatusQuery } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { RegistryDataGrid } from '../app/RegistryDataGrid'
import { useDocumentTitle } from '../app/documentTitle'
import { formatTimestamp } from '../format/dateTime'

type ExportedRow = SyncStatusQuery['syncStatus']['exportedSpaces'][number]
type Origin = SyncStatusQuery['syncStatus']['origins'][number]

const exportedColumns: GridColDef<ExportedRow>[] = [
  { field: 'spaceKey', headerName: 'Space', width: 140 },
  { field: 'lastOutboxSequence', headerName: 'Outbox position', width: 150 },
  {
    field: 'pendingEventCount',
    headerName: 'Pending events',
    width: 160,
    renderCell: (params) =>
      params.value > 0 ? (
        // Loud, not subtle (design.md §12): pending rows mean changes that
        // have not left this instance yet.
        <Chip label={`${params.value} pending`} size="small" color="warning" />
      ) : (
        <Chip label="drained" size="small" variant="outlined" color="success" />
      ),
  },
  {
    field: 'lastExportedBundle',
    headerName: 'Last exported bundle',
    width: 180,
    valueGetter: (value) => value ?? 'never',
  },
]

const originSpaceColumns: GridColDef[] = [
  { field: 'spaceKey', headerName: 'Space', width: 140 },
  { field: 'appliedSequence', headerName: 'Applied sequence', width: 160 },
]

function OriginCard({ origin }: { origin: Origin }) {
  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack spacing={1}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'baseline', flexWrap: 'wrap' }}>
          <Typography variant="h6" component="h3">
            Origin: {origin.originInstanceId}
          </Typography>
          <Typography variant="body2" color="text.secondary">
            last bundle #{origin.lastBundleNumber} · imported {formatTimestamp(origin.lastImportAtUtc)}
          </Typography>
        </Stack>
        <Typography variant="caption" color="text.secondary" sx={{ wordBreak: 'break-all' }}>
          Manifest hash (the link the next bundle must chain from): {origin.lastManifestHash}
        </Typography>
        <RegistryDataGrid
          aria-label={`Applied sequences from ${origin.originInstanceId}`}
          rowCount={origin.spaces.length}
          rows={origin.spaces}
          columns={originSpaceColumns}
          getRowId={(row) => row.spaceId}
        />
      </Stack>
    </Paper>
  )
}

/**
 * design.md §12: the admin sync status page — per exported space the
 * outbox position, pending event count and last drained bundle; per origin
 * instance the last bundle applied, its manifest hash, the import time and
 * per-space applied sequences. The server refuses non-admin callers and
 * audits every view as `sync.status`; a chain break refuses at import time
 * (RocketWiki.Sync exits 2), so what this page can surface loudly is
 * un-exported pending work and how stale each origin's last import is.
 */
export function SyncStatusPage() {
  useDocumentTitle('Sync status')
  const [{ data, fetching, error }] = useSyncStatusQuery()

  if (error) {
    return <Alert severity="info">{describeLoadFailure('SYNC_STATUS').summary}</Alert>
  }

  const status = data?.syncStatus

  return (
    <Stack spacing={3}>
      <PageHeader
        title="Sync status"
        description={status ? <>This instance: <strong>{status.localInstanceId}</strong></> : undefined}
      />

      <Stack spacing={1}>
        {/* `component="h2"` — a bare `variant="h6"` renders an <h6> element, so
            these sections were jumping the document straight from h1 to h6.
            Same fix on every section heading in this file. */}
        <Typography variant="h6" component="h2">
          Exported spaces (low → high outbox)
        </Typography>
        {status && status.exportedSpaces.length === 0 && (
          <Typography color="text.secondary">No spaces are flagged for export from this instance.</Typography>
        )}
        <RegistryDataGrid
          aria-label="Exported spaces"
          rowCount={status?.exportedSpaces.length ?? 0}
          rows={status?.exportedSpaces ?? []}
          columns={exportedColumns}
          getRowId={(row) => row.spaceId}
          loading={fetching}
        />
      </Stack>

      <Stack spacing={1}>
        <Typography variant="h6" component="h2">
          Imported origins (bundles applied here)
        </Typography>
        {status && status.origins.length === 0 && (
          <Typography color="text.secondary">No sync bundles have been imported into this instance.</Typography>
        )}
        {status?.origins.map((origin) => <OriginCard key={origin.originInstanceId} origin={origin} />)}
      </Stack>
    </Stack>
  )
}
