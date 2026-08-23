import { useParams } from 'react-router-dom'
import { Alert, Stack, Typography } from '@mui/material'

/**
 * design.md §13: per-page lossy-conversion findings from the Confluence
 * importer. Deliberately inert: the importer is milestone 5 ("not
 * started", design.md §16) and the real schema has no `importReports`
 * query — the placeholder-era UI rendered a shape that never existed
 * anywhere but the placeholder file. This page states that plainly rather
 * than showing a permanently-empty report.
 */
export function ImportReportPage() {
  const { spaceKey } = useParams<{ spaceKey: string }>()

  if (!spaceKey) return null

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">
        Import report: {spaceKey}
      </Typography>
      <Alert severity="info">
        Confluence migration (design.md §13) hasn't started yet — there's no importer, and no import-report API.
        This page will show per-page lossy-conversion findings once milestone 5 lands.
      </Alert>
    </Stack>
  )
}
