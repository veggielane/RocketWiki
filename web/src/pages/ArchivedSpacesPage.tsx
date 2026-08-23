import { Alert, Stack, Typography } from '@mui/material'

/**
 * NOTE (schema reconciliation): deliberately inert. The real schema has a
 * `restoreSpace` mutation but NO query that lists archived spaces —
 * SpaceReads excludes them via the EF query filter (whether previous
 * viewers should still see archived spaces is design.md §6.5.1's stated
 * open question, and exclusion is the backend's conservative default) — so
 * there is no way to obtain an archived space's id to restore it from
 * here. Reported as a contract gap; this page states the situation rather
 * than pretending an empty list means "nothing archived".
 */
export function ArchivedSpacesPage() {
  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">
        Archived spaces
      </Typography>
      <Alert severity="info">
        The API doesn't list archived spaces yet, so they can't be shown or restored from here. Restoring is
        possible server-side (design.md §6.5.1) — ask an operator, or wait for the archived-spaces listing to land.
      </Alert>
    </Stack>
  )
}
