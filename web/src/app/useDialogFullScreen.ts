import { useMediaQuery, useTheme } from '@mui/material'

/**
 * Whether a dialog should take the whole screen at this width.
 *
 * For dialogs that carry a FORM — the move picker, the fence-insert dialogs,
 * the create-page dialog, the stale-revision merge flow with its diff. Below
 * `sm` these are multi-field layouts inside a box with a 32px viewport margin,
 * which is most of a phone spent on chrome around a scrolling sliver.
 *
 * Deliberately NOT applied to the short confirmations (ConfirmDialog,
 * DeletePageDialog, the replica explainer): Material's own guidance keeps
 * simple dialogs centred, and a full screen given over to one sentence and two
 * buttons reads as a page the user has navigated to rather than a question they
 * have been asked.
 */
export function useDialogFullScreen(): boolean {
  const theme = useTheme()
  return useMediaQuery(theme.breakpoints.down('sm'))
}
