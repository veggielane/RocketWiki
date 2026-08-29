import { useParams } from 'react-router-dom'
import { Alert, Stack } from '@mui/material'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'

/**
 * design.md §13: per-page lossy-conversion findings from the Confluence
 * importer. Deliberately inert: the importer is milestone 5 ("not
 * started", design.md §16) and the real schema has no `importReports`
 * query — the placeholder-era UI rendered a shape that never existed
 * anywhere but the placeholder file. This page states that plainly rather
 * than showing a permanently-empty report.
 */
export function ImportReportPage() {
  useDocumentTitle('Import report')
  const { spaceKey } = useParams<{ spaceKey: string }>()

  if (!spaceKey) return null

  return (
    <Stack spacing={2}>
      <PageHeader title="Import report" subject={{ label: spaceKey, to: `/spaces/${spaceKey}` }} />
      <Alert severity="info">
        Confluence migration hasn't started yet — there's no importer, and no import-report API. This page will
        show per-page lossy-conversion findings once it lands.
      </Alert>
    </Stack>
  )
}
