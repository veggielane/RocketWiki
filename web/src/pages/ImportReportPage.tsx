import { useParams, Link as RouterLink } from 'react-router-dom'
import { Alert, Chip, List, ListItem, ListItemText, Paper, Skeleton, Stack, Typography } from '@mui/material'
import { useImportReportsQuery } from '../graphql/generated/graphql'

const SEVERITY_COLOR: Record<string, 'error' | 'warning' | 'info'> = {
  error: 'error',
  warning: 'warning',
  info: 'info',
}

/**
 * design.md §13: per-page lossy-conversion findings from the Confluence
 * importer. **Very provisional** — the importer that produces this report
 * is being built by another agent right now, so treat every field name
 * and shape here as a guess more than the rest of the placeholder schema
 * (see schema.placeholder.graphql's note on `Query.importReports`).
 */
export function ImportReportPage() {
  const { spaceKey } = useParams<{ spaceKey: string }>()
  const [{ data, fetching, error }] = useImportReportsQuery({ variables: { spaceKey: spaceKey ?? '' }, pause: !spaceKey })

  if (!spaceKey) return null

  if (fetching) {
    return <Skeleton variant="rectangular" height={300} />
  }

  if (error || !data) {
    return <Alert severity="info">Couldn't load import reports — there's no live API in this environment yet.</Alert>
  }

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">
        Import report: {spaceKey}
      </Typography>

      {data.importReports.length === 0 && (
        <Typography color="text.secondary">No imports recorded for this space.</Typography>
      )}

      {data.importReports.map((report) => (
        <Paper key={report.id} variant="outlined" sx={{ p: 2 }}>
          <Typography variant="subtitle1" gutterBottom>
            {new Date(report.importedAtUtc).toLocaleString()} — {report.pageCount} page
            {report.pageCount === 1 ? '' : 's'}
          </Typography>
          {report.findings.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              No lossy-conversion findings — this import round-tripped cleanly.
            </Typography>
          ) : (
            <List dense disablePadding>
              {report.findings.map((finding, i) => (
                <ListItem key={i} disableGutters>
                  <Stack direction="row" spacing={1} sx={{ alignItems: 'center', width: '100%' }}>
                    <Chip
                      size="small"
                      label={finding.severity}
                      color={SEVERITY_COLOR[finding.severity] ?? 'default'}
                      variant="outlined"
                    />
                    <ListItemText
                      primary={
                        finding.pageId ? (
                          <RouterLink to={`/pages/${finding.pageId}`}>{finding.pageTitle}</RouterLink>
                        ) : (
                          finding.pageTitle
                        )
                      }
                      secondary={finding.message}
                    />
                  </Stack>
                </ListItem>
              ))}
            </List>
          )}
        </Paper>
      ))}
    </Stack>
  )
}
