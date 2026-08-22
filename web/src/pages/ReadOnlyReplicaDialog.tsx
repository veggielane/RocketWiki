import { Button, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle } from '@mui/material'

export interface ReadOnlyReplicaDialogProps {
  open: boolean
  originInstanceId: string | null
  onClose: () => void
}

/**
 * `ReadOnlyReplicaError`: an explanatory banner-in-dialog form, never a raw
 * error toast — replicated spaces are always read-only (design.md §12), so
 * this explains *why* rather than just refusing the save.
 */
export function ReadOnlyReplicaDialog({ open, originInstanceId, onClose }: ReadOnlyReplicaDialogProps) {
  return (
    <Dialog open={open} onClose={onClose}>
      <DialogTitle>This space is read-only</DialogTitle>
      <DialogContent>
        <DialogContentText>
          This page belongs to a space mirrored from instance <strong>{originInstanceId}</strong>. Replicated spaces
          are always read-only — edit the page on its origin instance instead.
        </DialogContentText>
      </DialogContent>
      <DialogActions>
        <Button autoFocus onClick={onClose}>
          Close
        </Button>
      </DialogActions>
    </Dialog>
  )
}
