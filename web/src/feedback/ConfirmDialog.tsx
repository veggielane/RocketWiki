import { useState, type ReactNode } from 'react'
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
  /**
   * What the dialog said while it was open, kept for the close transition.
   *
   * Callers derive the title from the same state their confirm handler clears —
   * `Delete :${name}:?` from a `name` that becomes null the moment you press
   * Delete. MUI keeps the paper mounted for ~200ms while it fades, so the last
   * thing the user read on the way out was `Delete :null:?`. Holding the open
   * content here fixes it for every caller at once, rather than asking each one
   * to keep a shadow copy of state it has just finished with.
   *
   * Keyed on the title, which is the identity of what is being confirmed — the
   * body and the verb come from the same state in every caller, so a title that
   * has not changed means none of them have.
   */
  const [held, setHeld] = useState({ title, children, confirmLabel })
  const [heldTitle, setHeldTitle] = useState(title)
  if (open && heldTitle !== title) {
    setHeldTitle(title)
    setHeld({ title, children, confirmLabel })
  }
  const shown = open ? { title, children, confirmLabel } : held

  // Centred at every width, deliberately — see app/useDialogFullScreen.ts. A
  // whole phone screen given over to one sentence and two buttons reads as a
  // page you navigated to rather than a question you were asked.
  return (
    <Dialog open={open} onClose={onCancel}>
      <DialogTitle>{shown.title}</DialogTitle>
      <DialogContent>
        <DialogContentText component="div">{shown.children}</DialogContentText>
      </DialogContent>
      <DialogActions>
        {/* Default focus on Cancel, never on the destructive action — a stray
            Enter (easy to hit while already anxious about what you are about to
            do) must not confirm. */}
        <Button autoFocus onClick={onCancel} disabled={busy}>
          Cancel
        </Button>
        {/* `loading`, not just `disabled`: a button that greys out on click and
            then sits there says nothing about whether anything is happening. */}
        <Button color={tone} variant="contained" loading={busy} onClick={onConfirm}>
          {shown.confirmLabel}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
