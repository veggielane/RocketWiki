import { Link as RouterLink } from 'react-router-dom'
import { Button, Stack, Typography } from '@mui/material'

/**
 * Used both for genuinely unmatched routes (the router's catch-all) and for
 * an authorization gate that fails (`AccessGate`, `RequireInstanceAdmin`).
 * That's deliberate, not a shortcut: design.md §6.7's "absent rather than
 * forbidden" means a page/space a caller has no rights to must look
 * identical to one that doesn't exist — a distinct "403 Forbidden" screen
 * would itself leak that the thing is there. This is a UX nicety, not a
 * security boundary; the server is the real enforcement point (it must
 * never return the sensitive data in the first place), same rule as
 * everywhere else in this app.
 */
export function NotFoundPage() {
  return (
    <Stack spacing={2} sx={{ alignItems: 'center', textAlign: 'center', py: 8 }}>
      <Typography variant="h3" component="h1">
        404
      </Typography>
      <Typography color="text.secondary">This page doesn't exist, or you don't have access to it.</Typography>
      <Button component={RouterLink} to="/" variant="outlined">
        Back to spaces
      </Button>
    </Stack>
  )
}
