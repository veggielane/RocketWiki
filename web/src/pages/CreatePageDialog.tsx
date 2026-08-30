import { useState } from 'react'
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  MenuItem,
  Stack,
  TextField,
} from '@mui/material'
import { canonicalSlug, isReservedSlug, isUsableSlug, slugifyTitle } from './pageSlug'
import type { ParentOption } from './parentOptions'
import { PageIconPicker } from './PageIconPicker'
import type { PageIcon } from '../graphql/generated/graphql'
import { useDialogFullScreen } from '../app/useDialogFullScreen'

/** What the dialog hands back, named so its two callers cannot drift from it. */
export interface CreatePageValues {
  title: string
  slug: string
  parentPageId: string | null
  icon: PageIcon | null
}

export interface CreatePageDialogProps {
  open: boolean
  /** Names where the dialog was opened from, so the title says so. */
  parentLabel: string
  /**
   * Every page in the space the new one could hang under, plus the space root.
   * Omitted entirely (or a single root entry) hides the picker — there is
   * nothing to choose between.
   */
  parentOptions?: ParentOption[]
  /**
   * Pre-selected parent: the page you opened this from, or null for a top-level
   * page. Creating from a page should default to that page, which is the whole
   * point of "Add child page".
   */
  defaultParentId?: string | null
  /** Server-side refusal to show inline; null clears it. */
  error?: string | null
  busy?: boolean
  onCancel: () => void
  onConfirm: (values: CreatePageValues) => void
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
  parentOptions,
  defaultParentId = null,
  error,
  busy = false,
  onCancel,
  onConfirm,
}: CreatePageDialogProps) {
  const fullScreen = useDialogFullScreen()
  const [title, setTitle] = useState('')
  const [slug, setSlug] = useState('')
  const [slugEdited, setSlugEdited] = useState(false)
  const [parentPageId, setParentPageId] = useState<string | null>(defaultParentId)
  const [icon, setIcon] = useState<PageIcon | null>(null)

  // The caller's default can arrive after the first render (the tree loads
  // asynchronously) and changes when a different page opens the dialog, so
  // track it rather than only seeding initial state — React's documented
  // "adjusting state when a prop changes" pattern, as used in UserAvatar.
  const [trackedDefault, setTrackedDefault] = useState(defaultParentId)
  if (trackedDefault !== defaultParentId) {
    setTrackedDefault(defaultParentId)
    setParentPageId(defaultParentId)
  }

  const reset = () => {
    setTitle('')
    setSlug('')
    setSlugEdited(false)
    setParentPageId(defaultParentId)
    setIcon(null)
  }

  const handleCancel = () => {
    reset()
    onCancel()
  }

  const effectiveSlug = slugEdited ? slug : slugifyTitle(title)
  const canCreate = title.trim().length > 0 && isUsableSlug(effectiveSlug) && !busy

  return (
    <Dialog open={open} onClose={handleCancel} fullWidth maxWidth="sm" fullScreen={fullScreen}>
      <DialogTitle>New page in {parentLabel}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {/* Inline, not a snackbar — a refusal is something to act on, and the
              dialog stays open with the typed values intact so the fix is a
              correction rather than a retype (web/README.md's feedback rule). */}
          {error && <Alert severity="error">{error}</Alert>}
          {/* Icon beside the title rather than under it: it is a property of
              the title line, and a full-width row of its own would give a
              two-field dialog the height of a form. */}
          <Stack direction="row" spacing={2}>
            <TextField
              autoFocus
              label="Title"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              sx={{ flex: 1 }}
            />
            <PageIconPicker value={icon} onChange={setIcon} disabled={busy} />
          </Stack>
          {parentOptions && parentOptions.length > 1 && (
            <TextField
              select
              label="Parent page"
              value={parentPageId ?? ''}
              onChange={(e) => setParentPageId(e.target.value === '' ? null : e.target.value)}
              fullWidth
              helperText="Where this page sits in the space's hierarchy."
            >
              {parentOptions.map((option) => (
                <MenuItem key={option.id ?? '__root__'} value={option.id ?? ''}>
                  {/* Non-breaking spaces, not padding: MUI renders the selected
                      option's text into the closed field too, and indentation
                      that lives in the row's styling would be lost there. */}
                  {' '.repeat(option.depth * 2)}
                  {option.title}
                </MenuItem>
              ))}
            </TextField>
          )}
          <TextField
            label="URL slug"
            value={effectiveSlug}
            onChange={(e) => {
              setSlugEdited(true)
              // Folded to lower case in the field itself, not just on submit.
              // The server stores slugs lowercased, so a field showing `My-Page`
              // while the page is created at `my-page` would be the UI promising
              // an address the server does not keep — and the first the author
              // hears of it is the URL after Create.
              setSlug(canonicalSlug(e.target.value))
            }}
            fullWidth
            helperText={
              title.trim().length > 0 && isReservedSlug(effectiveSlug)
                ? `"-" is reserved for this space's own settings pages — pick another slug.`
                : title.trim().length > 0 && !isUsableSlug(effectiveSlug)
                  ? 'This title has no characters a URL can use — type a slug.'
                  : 'The page address: /spaces/{key}/{slug}. Always lower case; derived from the title until you change it.'
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
          onClick={() => onConfirm({ title: title.trim(), slug: effectiveSlug.trim(), parentPageId, icon })}
        >
          Create
        </Button>
      </DialogActions>
    </Dialog>
  )
}
