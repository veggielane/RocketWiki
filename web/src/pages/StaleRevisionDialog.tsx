import { Button, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle } from '@mui/material'

export interface StaleRevisionDialogProps {
  open: boolean
  currentRevisionNumber: number | null
  onViewChanges: () => void
  onOverwriteAnyway: () => void
  onCopyAndCancel: () => void
  onClose: () => void
}

/**
 * `StaleRevisionError`: the merge flow (view their changes / overwrite /
 * copy my text and cancel), not an exception (design.md §5, §8).
 */
export function StaleRevisionDialog({
  open,
  currentRevisionNumber,
  onViewChanges,
  onOverwriteAnyway,
  onCopyAndCancel,
  onClose,
}: StaleRevisionDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle>Someone else saved changes first</DialogTitle>
      <DialogContent>
        <DialogContentText sx={{ mb: 2 }}>
          This page has been saved as revision {currentRevisionNumber} since you started editing. Choose how to
          proceed:
        </DialogContentText>
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2, justifyContent: 'flex-start', gap: 1 }}>
        {/* Default focus on the non-destructive option — this dialog
            appears mid-edit, when a stray Enter keypress is exactly the
            kind of mistake someone stressed about losing work makes.
            "Overwrite anyway" must never be reachable by pressing Enter
            without an intentional Tab first. */}
        <Button autoFocus variant="outlined" onClick={onViewChanges}>
          View their changes
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
