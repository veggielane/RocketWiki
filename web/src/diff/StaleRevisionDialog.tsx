import {
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Stack,
  Typography,
} from '@mui/material'
import { MarkdownDiffView } from './MarkdownDiffView'

export interface StaleRevisionDialogProps {
  open: boolean
  currentRevisionNumber: number | null
  /** The page title the user's save carried. */
  yourTitle: string
  /** The user's editor content at conflict time — raw Markdown. */
  yourDraft: string
  /** From StaleRevisionError: the latest revision's title, when it changed too. */
  theirTitle: string | null
  /** From StaleRevisionError: the latest revision's content. */
  theirContent: string | null
  onOverwriteAnyway: () => void
  onCopyAndCancel: () => void
  onKeepEditing: () => void
}

/**
 * `StaleRevisionError`: the merge flow (view their changes / overwrite /
 * copy my text), not an exception (design.md §5, §8). "View their changes"
 * is not a further click away — the error already carries the latest
 * content, so the diff against the user's draft IS the dialog body: the
 * evidence sits right next to the choice it informs. Both sides are page
 * content (trust boundary), rendered as text only by MarkdownDiffView.
 */
export function StaleRevisionDialog({
  open,
  currentRevisionNumber,
  yourTitle,
  yourDraft,
  theirTitle,
  theirContent,
  onOverwriteAnyway,
  onCopyAndCancel,
  onKeepEditing,
}: StaleRevisionDialogProps) {
  const titlesDiffer = theirTitle !== null && theirTitle !== yourTitle
  return (
    <Dialog open={open} onClose={onKeepEditing} maxWidth="md" fullWidth>
      <DialogTitle>Someone else saved changes first</DialogTitle>
      <DialogContent>
        <DialogContentText sx={{ mb: 2 }}>
          This page has been saved as revision {currentRevisionNumber} since you started editing. Their changes are
          compared with your draft below — lines marked + exist only in their version, lines marked − only in yours.
        </DialogContentText>

        {titlesDiffer && (
          <Stack spacing={0.5} sx={{ mb: 2 }}>
            <Typography variant="body2">
              <Box component="span" sx={{ color: 'text.secondary' }}>
                Their title:{' '}
              </Box>
              {theirTitle}
            </Typography>
            <Typography variant="body2">
              <Box component="span" sx={{ color: 'text.secondary' }}>
                Your title:{' '}
              </Box>
              {yourTitle}
            </Typography>
          </Stack>
        )}

        {theirContent !== null ? (
          <MarkdownDiffView
            yourDraft={yourDraft}
            theirContent={theirContent}
            label="Their changes compared with your draft"
          />
        ) : (
          <DialogContentText>Their latest content isn't available to compare.</DialogContentText>
        )}
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2, justifyContent: 'flex-start', gap: 1 }}>
        {/* Default focus on the non-destructive option — this dialog
            appears mid-edit, when a stray Enter keypress is exactly the
            kind of mistake someone stressed about losing work makes.
            Neither "Overwrite anyway" (clobbers theirs) nor "Copy my text
            and cancel" (abandons the editor) may be reachable by pressing
            Enter without an intentional Tab first. */}
        <Button autoFocus variant="outlined" onClick={onKeepEditing}>
          Keep editing
        </Button>
        <Button variant="outlined" color="warning" onClick={onOverwriteAnyway}>
          Overwrite anyway
        </Button>
        <Button variant="text" onClick={onCopyAndCancel}>
          Copy my text and cancel
        </Button>
      </DialogActions>
    </Dialog>
  )
}
