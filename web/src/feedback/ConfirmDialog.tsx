import type { ReactNode } from 'react'
import { Button, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle } from '@mui/material'

export interface ConfirmDialogProps {
  open: boolean
  /** Phrased as the question being asked — `Delete "Runbook"?`. */
  title: string
  /** What actually happens, and what can be undone. */
  children: ReactNode
  /** The verb, on the button. */
  confirmLabel: string
  /**
   * `error` for something irreversible or destructive, `warning` for something
   * reversible but consequential (archiving a space). Both get a filled button:
   * the quiet variant on a delete confirmation made the most permanent action on
   * screen the least visible one.
   */
  tone?: 'error' | 'warning'
  busy?: boolean
  onConfirm: () => void
  onCancel: () => void
}

/**
 * One shape for "are you sure?".
 *
 * Three confirmations existed with three different treatments — the property-key
 * delete had no default focus and a text-variant confirm button, the page delete
 * had both, the space archive had a `warning`-coloured contained button. The
 * outlier mattered: without `autoFocus` on Cancel, whatever the dialog happened
 * to focus first could be confirmed by a stray Enter, and the quietest button on
 * the screen was the irreversible one.
 *
 * So the safe option always takes focus and the destructive one is always
 * filled. Callers that need more than a sentence and a verb — the delete dialog
 * carries a blocked-descendant count (design.md §6.4.1), the stale-revision
 * dialog offers three ways out — keep their own component; this is for the
 * ordinary case, which is most of them.
 */
export function ConfirmDialog({
  open,
  title,
  children,
  confirmLabel,
  tone = 'error',
  busy,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  // Centred at every width, deliberately — see app/useDialogFullScreen.ts. A
  // whole phone screen given over to one sentence and two buttons reads as a
  // page you navigated to rather than a question you were asked.
  return (
    <Dialog open={open} onClose={onCancel}>
      <DialogTitle>{title}</DialogTitle>
      <DialogContent>
        <DialogContentText component="div">{children}</DialogContentText>
      </DialogContent>
      <DialogActions>
        {/* Default focus on Cancel, never on the destructive action — a stray
            Enter (easy to hit while already anxious about what you are about to
            do) must not confirm. */}
        <Button autoFocus onClick={onCancel}>
          Cancel
        </Button>
        <Button color={tone} variant="contained" disabled={busy} onClick={onConfirm}>
          {confirmLabel}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
