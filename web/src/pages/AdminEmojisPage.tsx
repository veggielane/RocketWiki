import { useRef, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  IconButton,
  Paper,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined'
import { type GridColDef } from '@mui/x-data-grid'
import { useCustomEmojisQuery, type CustomEmojisQuery } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { ConfirmDialog } from '../feedback/ConfirmDialog'
import { PageHeader } from '../app/PageHeader'
import { RegistryDataGrid } from '../app/RegistryDataGrid'
import { useDocumentTitle } from '../app/documentTitle'
import { EMOJI_NAME_MAX_LENGTH, isValidEmojiName } from '../emoji/grammar'
import { EMOJI_ACCEPTED_TYPES, EmojiMutationFailure, deleteEmoji, uploadEmoji } from '../emoji/emojiApi'
import { EmojiImg } from '../emoji/EmojiImg'
import { formatBytes } from '../attachments/formatBytes'

type EmojiRow = CustomEmojisQuery['customEmojis'][number]

const NAME_RULES = `Lowercase letters, digits, "_" and "-" only (1–${EMOJI_NAME_MAX_LENGTH} characters).`

/**
 * Instance-admin curation of the `:name:` registry (design.md §19; reached
 * from /admin, which RequireInstanceAdmin gates at the router). The list is
 * the same generated `CustomEmojis` query every renderer uses — refetching
 * it network-only after a mutation updates urql's document cache, which
 * also flows into the module registry via AppShell's feed, so open editors
 * re-decorate live. The binary mutations are the plain HTTP routes
 * (emoji/emojiApi.ts): POST sends **raw bytes**, and the server is the
 * enforcement point — the client-side grammar check only fails fast with
 * the same rule the server would state.
 *
 * Deleting is deliberately un-dramatic (confirm, then gone): content
 * carrying the name keeps rendering it as literal text, harmless by
 * construction, which is also why the server hard-deletes.
 */
export function AdminEmojisPage() {
  useDocumentTitle('Custom emojis')
  const [{ data, fetching, error }, refetch] = useCustomEmojisQuery()
  const fileInputRef = useRef<HTMLInputElement>(null)
  const [name, setName] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const [feedback, setFeedback] = useState<{ severity: 'success' | 'warning'; message: string } | null>(null)
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null)

  const nameInvalid = name.length > 0 && !isValidEmojiName(name)

  const describeFailure = (err: unknown): string => {
    if (err instanceof EmojiMutationFailure) {
      switch (err.detail.kind) {
        case 'tooLarge': {
          const cap = err.detail.maxSizeBytes
          return cap !== null
            ? `That image is too big — emoji uploads can be up to ${formatBytes(cap)}.`
            : 'That image is too big for an emoji.'
        }
        case 'nameTaken':
          return err.detail.message ?? `An emoji named '${name}' already exists.`
        case 'forbidden':
          return err.detail.message ? `Not permitted: ${err.detail.message}` : 'Not permitted.'
        default:
          return err.detail.message ?? 'The upload was refused.'
      }
    }
    return "Couldn't reach the API."
  }

  const handleUpload = async () => {
    if (!file || !isValidEmojiName(name)) return
    setBusy(true)
    setFeedback(null)
    try {
      const created = await uploadEmoji(name, file)
      setFeedback({ severity: 'success', message: `Added :${created.name}:` })
      setName('')
      setFile(null)
      refetch({ requestPolicy: 'network-only' })
    } catch (err) {
      setFeedback({ severity: 'warning', message: describeFailure(err) })
    } finally {
      setBusy(false)
    }
  }

  const handleDelete = async (emojiName: string) => {
    setConfirmDelete(null)
    setBusy(true)
    setFeedback(null)
    try {
      await deleteEmoji(emojiName)
      setFeedback({ severity: 'success', message: `Deleted :${emojiName}: — existing content shows the literal text.` })
      refetch({ requestPolicy: 'network-only' })
    } catch (err) {
      setFeedback({ severity: 'warning', message: describeFailure(err) })
    } finally {
      setBusy(false)
    }
  }

  const columns: GridColDef<EmojiRow>[] = [
    {
      field: 'preview',
      headerName: 'Preview',
      width: 90,
      sortable: false,
      renderCell: (params) => <EmojiImg name={params.row.name} etag={params.row.etag} size={24} />,
    },
    {
      field: 'name',
      headerName: 'Name',
      width: 260,
      valueGetter: (value) => `:${value}:`,
    },
    {
      field: 'actions',
      headerName: '',
      width: 80,
      sortable: false,
      renderCell: (params) => (
        <Tooltip title={`Delete :${params.row.name}:`}>
          <IconButton
            size="small"
            color="error"
            aria-label={`Delete :${params.row.name}:`}
            disabled={busy}
            onClick={() => setConfirmDelete(params.row.name)}
          >
            <DeleteOutlinedIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      ),
    },
  ]

  const rows = data?.customEmojis ?? []

  return (
    <Stack spacing={2}>
      <PageHeader
        title="Custom emojis"
        description={
          <>
            Anyone signed in can use these as <code>:name:</code> in pages and comments. PNG, JPEG, WebP, or GIF
            (animated GIFs keep their frames); images are squared and re-encoded server-side. Definitions are
            instance-local — synced content falls back to the literal text where a name isn't defined.
          </>
        }
      />

      {feedback && (
        <Alert severity={feedback.severity} onClose={() => setFeedback(null)}>
          {feedback.message}
        </Alert>
      )}

      <Paper variant="outlined" sx={{ p: 2, maxWidth: 640 }}>
        <Typography variant="h6" component="h2" sx={{ mb: 1 }}>
          Add an emoji
        </Typography>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start', flexWrap: 'wrap' }}>
          <TextField
            label="Name"
            size="small"
            value={name}
            onChange={(e) => setName(e.target.value)}
            error={nameInvalid}
            helperText={nameInvalid ? NAME_RULES : `Used as :name: — ${NAME_RULES.toLowerCase()}`}
            sx={{ minWidth: 260 }}
          />
          <input
            ref={fileInputRef}
            type="file"
            accept={EMOJI_ACCEPTED_TYPES.join(',')}
            hidden
            aria-label="Choose emoji image"
            onChange={(e) => {
              const chosen = e.target.files?.[0] ?? null
              setFeedback(null)
              if (chosen && !EMOJI_ACCEPTED_TYPES.includes(chosen.type)) {
                setFeedback({ severity: 'warning', message: 'Emojis can be PNG, JPEG, WebP, or GIF images.' })
              } else {
                setFile(chosen)
              }
              e.target.value = ''
            }}
          />
          <Button variant="outlined" size="small" onClick={() => fileInputRef.current?.click()} disabled={busy}>
            {file ? file.name : 'Choose image'}
          </Button>
          <Button
            variant="contained"
            size="small"
            onClick={() => void handleUpload()}
            disabled={busy || !file || !isValidEmojiName(name)}
          >
            Upload
          </Button>
        </Stack>
      </Paper>

      {error && <Alert severity="info">{describeLoadFailure('EMOJI_REGISTRY').summary}</Alert>}
      {!error && rows.length === 0 && !fetching && (
        <Typography color="text.secondary">
          No custom emojis yet — upload one above to make it available as :name: everywhere.
        </Typography>
      )}
      {rows.length > 0 && (
        <Box sx={{ maxWidth: 640 }}>
          <RegistryDataGrid
            aria-label="Custom emoji registry"
            rowCount={rows.length}
            rows={rows}
            columns={columns}
            getRowId={(row) => row.name}
            loading={fetching}
          />
        </Box>
      )}

      <ConfirmDialog
        open={confirmDelete !== null}
        title={`Delete :${confirmDelete}:?`}
        confirmLabel="Delete"
        onCancel={() => setConfirmDelete(null)}
        onConfirm={() => confirmDelete && void handleDelete(confirmDelete)}
      >
        Pages and comments using it will show the literal <code>:{confirmDelete}:</code> text instead. The name
        becomes available again immediately.
      </ConfirmDialog>
    </Stack>
  )
}
