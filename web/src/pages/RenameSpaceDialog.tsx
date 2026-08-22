import { Button, Dialog, DialogActions, DialogContent, DialogTitle, TextField } from '@mui/material'

export interface RenameSpaceDialogProps {
  open: boolean
  spaceName: string
  value: string
  onValueChange: (value: string) => void
  onCancel: () => void
  onConfirm: () => void
}

/**
 * design.md §6.5.1: rename requires instance admin OR this space's own
 * space-admin (`canManageAccess`) — enforced by the caller deciding whether
 * to offer the Rename button at all; this dialog is presentation only.
 */
export function RenameSpaceDialog({ open, spaceName, value, onValueChange, onCancel, onConfirm }: RenameSpaceDialogProps) {
  return (
    <Dialog open={open} onClose={onCancel}>
      <DialogTitle>Rename "{spaceName}"</DialogTitle>
      <DialogContent>
        <TextField
          autoFocus
          label="Name"
          value={value}
          onChange={(e) => onValueChange(e.target.value)}
          fullWidth
          sx={{ mt: 1 }}
        />
      </DialogContent>
      <DialogActions>
        <Button onClick={onCancel}>Cancel</Button>
        <Button variant="contained" disabled={value.trim().length === 0} onClick={onConfirm}>
          Rename
        </Button>
      </DialogActions>
    </Dialog>
  )
}
