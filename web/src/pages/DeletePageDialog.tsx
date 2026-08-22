import { Alert, Button, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle } from '@mui/material'
import { describeDeleteOutcome } from '../trash/describeDeleteOutcome'

export interface DeletePageDialogProps {
  open: boolean
  onClose: () => void
  pageTitle: string
  /** Whether this page has any direct children — the exact subtree size is a server-side count we don't have client-side. */
  hasChildren: boolean
  onConfirm: () => void
  /** Set only after a delete attempt returns a partial result — see describeDeleteOutcome.ts. */
  blockedDescendantCount?: number | null
  deleting?: boolean
}

/**
 * design.md §6.4.1: deleting a page always cascades to its whole subtree in
 * one operation (the trash entry is a batch, see TrashPage.tsx) — this
 * warns about that up front rather than surprising the admin after the
 * fact. If the delete comes back partially blocked, the outcome is a
 * count only, never which descendants — see describeDeleteOutcome.ts.
 */
export function DeletePageDialog({
  open,
  onClose,
  pageTitle,
  hasChildren,
  onConfirm,
  blockedDescendantCount,
  deleting,
}: DeletePageDialogProps) {
  const blockedMessage = describeDeleteOutcome(blockedDescendantCount)

  return (
    <Dialog open={open} onClose={onClose}>
      <DialogTitle>Delete "{pageTitle}"?</DialogTitle>
      <DialogContent>
        <DialogContentText>
          {hasChildren
            ? `This deletes "${pageTitle}" and everything nested under it, as one operation. The whole batch can be restored together from the space's trash within 30 days.`
            : `"${pageTitle}" can be restored from the space's trash within 30 days.`}
        </DialogContentText>
        {blockedMessage && (
          <Alert severity="warning" sx={{ mt: 2 }}>
            {blockedMessage}
          </Alert>
        )}
      </DialogContent>
      <DialogActions>
        {/* Default focus on Cancel, not the destructive action — a stray
            Enter keypress (easy to trigger while already stressed about
            the page you're about to delete) must not confirm a delete. */}
        <Button autoFocus onClick={onClose}>
          Cancel
        </Button>
        <Button color="error" variant="contained" disabled={deleting} onClick={onConfirm}>
          Delete
        </Button>
      </DialogActions>
    </Dialog>
  )
}
