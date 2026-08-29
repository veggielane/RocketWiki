import { useState } from 'react'
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
} from '@mui/material'
import { isUsableSlug, slugifyTitle } from './pageSlug'

export interface CreatePageDialogProps {
  open: boolean
  /** Names what the new page will be created under, so the dialog can say so. */
  parentLabel: string
  /** Server-side refusal to show inline; null clears it. */
  error?: string | null
  busy?: boolean
  onCancel: () => void
  onConfirm: (values: { title: string; slug: string }) => void
}

/**
 * Presentation only — the caller owns the mutation and decides whether to offer
 * page creation at all (it needs canEdit on the space, which the server enforces
 * regardless). Same split as RenameSpaceDialog.
 *
 * The slug is derived from the title as you type but stays editable, and once
 * edited by hand it stops tracking the title: a slug is part of the page's URL,
 * so silently rewriting one someone deliberately set would be the wrong kind of
 * helpful.
 */
export function CreatePageDialog({
  open,
  parentLabel,
  error,
  busy = false,
  onCancel,
  onConfirm,
}: CreatePageDialogProps) {
  const [title, setTitle] = useState('')
  const [slug, setSlug] = useState('')
  const [slugEdited, setSlugEdited] = useState(false)

  const reset = () => {
    setTitle('')
    setSlug('')
    setSlugEdited(false)
  }

  const handleCancel = () => {
    reset()
    onCancel()
  }

  const effectiveSlug = slugEdited ? slug : slugifyTitle(title)
  const canCreate = title.trim().length > 0 && isUsableSlug(effectiveSlug) && !busy

  return (
    <Dialog open={open} onClose={handleCancel} fullWidth maxWidth="sm">
      <DialogTitle>New page in {parentLabel}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {/* Inline, not a snackbar — a refusal is something to act on, and the
              dialog stays open with the typed values intact so the fix is a
              correction rather than a retype (web/README.md's feedback rule). */}
          {error && <Alert severity="error">{error}</Alert>}
          <TextField
            autoFocus
            label="Title"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            fullWidth
          />
          <TextField
            label="URL slug"
            value={effectiveSlug}
            onChange={(e) => {
              setSlugEdited(true)
              setSlug(e.target.value)
            }}
            fullWidth
            helperText={
              title.trim().length > 0 && !isUsableSlug(effectiveSlug)
                ? 'This title has no characters a URL can use — type a slug.'
                : 'Part of the page address. Derived from the title until you change it.'
            }
            error={title.trim().length > 0 && !isUsableSlug(effectiveSlug)}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={handleCancel}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!canCreate}
          onClick={() => onConfirm({ title: title.trim(), slug: effectiveSlug.trim() })}
        >
          Create
        </Button>
      </DialogActions>
    </Dialog>
  )
}
