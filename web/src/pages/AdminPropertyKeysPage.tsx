import { useState } from 'react'
import {
  Alert,
  Box,
  Button,
  IconButton,
  Paper,
  Snackbar,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined'
import { type GridColDef } from '@mui/x-data-grid'
import {
  useCreatePagePropertyKeyMutation,
  useDeletePagePropertyKeyMutation,
  usePagePropertyKeysQuery,
  type PagePropertyKeysQuery,
} from '../graphql/generated/graphql'
import { describeMutationError } from '../graphql/mutationError'
import { describeLoadFailure, describeWriteFailure } from '../feedback/unavailableCopy'
import { SNACKBAR_AUTO_HIDE_MS } from '../feedback/snackbar'
import { ConfirmDialog } from '../feedback/ConfirmDialog'
import { PageHeader } from '../app/PageHeader'
import { RegistryDataGrid } from '../app/RegistryDataGrid'
import { useDocumentTitle } from '../app/documentTitle'
import { MAX_PROPERTY_KEY_DESCRIPTION_LENGTH, MAX_PROPERTY_KEY_LENGTH } from '../properties/propertyLimits'

type KeyRow = PagePropertyKeysQuery['pagePropertyKeys'][number]

/**
 * Instance-admin curation of the page-property key registry (design.md §20.1;
 * reached from /admin, which RequireInstanceAdmin gates at the router). Keys
 * are the vocabulary page editors pick from — free-form keys were rejected
 * precisely so `Owner`, `owner` and `Owner ` cannot all exist, and uniqueness
 * is enforced server-side on a normalized form, which is why a duplicate is
 * reported with the server's own message rather than guessed at here.
 *
 * Deleting a key that pages still use is REFUSED, never cascaded: §6.4.1's
 * "deletion is an explicit, audited operation, never a side effect". The
 * refusal names how many pages use it — and deliberately not which, since a
 * per-page answer would leak restricted pages (§6.7) — so that message is
 * shown verbatim.
 */
export function AdminPropertyKeysPage() {
  useDocumentTitle('Page property keys')
  const [{ data, fetching, error }, refetch] = usePagePropertyKeysQuery()
  const [, createKey] = useCreatePagePropertyKeyMutation()
  const [, deleteKey] = useDeletePagePropertyKeyMutation()
  const [key, setKey] = useState('')
  const [description, setDescription] = useState('')
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [refusal, setRefusal] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState<KeyRow | null>(null)

  const rows = data?.pagePropertyKeys ?? []
  const keyTooLong = key.trim().length > MAX_PROPERTY_KEY_LENGTH
  const descriptionTooLong = description.length > MAX_PROPERTY_KEY_DESCRIPTION_LENGTH

  const handleCreate = async () => {
    const trimmed = key.trim()
    if (trimmed.length === 0 || keyTooLong || descriptionTooLong || busy) return
    setBusy(true)
    setRefusal(null)
    try {
      const result = await createKey({
        // An omitted description is null, not "" — the field is nullable and
        // an empty string would render as a description that is simply blank.
        input: { key: trimmed, description: description.trim() || null },
      })
      if (result.error !== undefined) {
        setRefusal(describeWriteFailure('PROPERTY_KEY').summary)
        return
      }
      const refused = describeMutationError(result.data?.createPagePropertyKey.error)
      if (refused) {
        setRefusal(refused)
        return
      }
      setNotice(`Added ${result.data?.createPagePropertyKey.propertyKey?.key ?? trimmed}.`)
      setKey('')
      setDescription('')
      refetch({ requestPolicy: 'network-only' })
    } finally {
      setBusy(false)
    }
  }

  const handleDelete = async (row: KeyRow) => {
    // The dialog stays open until the server has answered — this delete can be
    // REFUSED (the key is in use), and closing first meant the question
    // disappeared before its answer arrived.
    setBusy(true)
    setRefusal(null)
    try {
      const result = await deleteKey({ keyId: row.id })
      if (result.error !== undefined) {
        setRefusal(describeWriteFailure('PROPERTY_KEY').summary)
        return
      }
      // The in-use refusal is a ValidationError whose message carries the page
      // count — the useful part, and the only part the server may disclose.
      const refused = describeMutationError(result.data?.deletePagePropertyKey.error)
      if (refused) {
        setRefusal(refused)
        return
      }
      setNotice(`Deleted ${row.key}.`)
      refetch({ requestPolicy: 'network-only' })
    } finally {
      setBusy(false)
      setConfirmDelete(null)
    }
  }

  const columns: GridColDef<KeyRow>[] = [
    { field: 'key', headerName: 'Key', width: 220 },
    {
      field: 'description',
      headerName: 'Description',
      flex: 1,
      minWidth: 220,
      valueGetter: (value: string | null) => value ?? '',
    },
    {
      field: 'actions',
      headerName: 'Actions',
      width: 90,
      sortable: false,
      renderCell: (params) => (
        <Tooltip title={`Delete ${params.row.key}`}>
          <span>
            <IconButton
              size="small"
              color="error"
              aria-label={`Delete ${params.row.key}`}
              disabled={busy}
              onClick={() => setConfirmDelete(params.row)}
            >
              <DeleteOutlinedIcon fontSize="small" />
            </IconButton>
          </span>
        </Tooltip>
      ),
    },
  ]

  return (
    <Stack spacing={2}>
      <PageHeader
        title="Page property keys"
        description="The vocabulary page editors pick from when they add key/value metadata to a page. Anyone signed in can see the key list — a key's existence says nothing about which pages use it — but only instance admins change it. Keys are unique case-insensitively, and values live on each page's properties screen, never in its text."
      />

      {refusal && (
        <Alert severity="warning" onClose={() => setRefusal(null)}>
          {refusal}
        </Alert>
      )}

      <Paper variant="outlined" sx={{ p: 2, maxWidth: 720 }}>
        <Typography variant="h6" component="h2" sx={{ mb: 1 }}>
          Add a key
        </Typography>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start', flexWrap: 'wrap' }}>
          <TextField
            label="Key"
            size="small"
            value={key}
            disabled={busy}
            error={keyTooLong}
            onChange={(e) => setKey(e.target.value)}
            helperText={
              keyTooLong
                ? `A property key may be at most ${MAX_PROPERTY_KEY_LENGTH} characters.`
                : 'Shown beside every page that carries a value, e.g. "Review Date".'
            }
            sx={{ minWidth: 240 }}
          />
          <TextField
            label="Description (optional)"
            size="small"
            value={description}
            disabled={busy}
            error={descriptionTooLong}
            onChange={(e) => setDescription(e.target.value)}
            helperText={
              descriptionTooLong
                ? `A description may be at most ${MAX_PROPERTY_KEY_DESCRIPTION_LENGTH} characters.`
                : 'What editors should put in this property.'
            }
            sx={{ minWidth: 280, flexGrow: 1 }}
          />
          <Button
            variant="contained"
            size="small"
            sx={{ mt: 0.5 }}
            disabled={busy || key.trim().length === 0 || keyTooLong || descriptionTooLong}
            onClick={() => void handleCreate()}
          >
            Add key
          </Button>
        </Stack>
      </Paper>

      {error && <Alert severity="info">{describeLoadFailure('PROPERTY_KEY_REGISTRY').summary}</Alert>}
      {!error && rows.length === 0 && !fetching && (
        <Typography color="text.secondary">
          No property keys yet — until one exists, page editors have no properties they can set.
        </Typography>
      )}
      {rows.length > 0 && (
        <Box sx={{ maxWidth: 720 }}>
          <RegistryDataGrid
            aria-label="Page property key registry"
            rowCount={rows.length}
            rows={rows}
            columns={columns}
            getRowId={(row) => row.id}
            loading={fetching}
          />
        </Box>
      )}

      <ConfirmDialog
        open={confirmDelete !== null}
        title={`Delete ${confirmDelete?.key}?`}
        confirmLabel="Delete"
        busy={busy}
        onCancel={() => setConfirmDelete(null)}
        onConfirm={() => confirmDelete && void handleDelete(confirmDelete)}
      >
        The key disappears from every editor's picker. If pages still carry a value for it, the delete is refused
        rather than taking those values with it — remove them first.
      </ConfirmDialog>

      <Snackbar open={notice !== null} autoHideDuration={SNACKBAR_AUTO_HIDE_MS} onClose={() => setNotice(null)}>
        <Alert severity="success" onClose={() => setNotice(null)}>
          {notice}
        </Alert>
      </Snackbar>
    </Stack>
  )
}
