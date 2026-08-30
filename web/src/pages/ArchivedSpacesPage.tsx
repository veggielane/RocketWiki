import { useState } from 'react'
import { Alert, Button, List, ListItem, ListItemText, Skeleton, Snackbar, Stack, Typography } from '@mui/material'
import { useArchivedSpacesQuery, useRestoreSpaceMutation } from '../graphql/generated/graphql'
import { describeLoadFailure, describeWriteFailure } from '../feedback/unavailableCopy'
import { SNACKBAR_AUTO_HIDE_MS } from '../feedback/snackbar'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { formatTimestamp } from '../format/dateTime'
import { describeMutationError } from '../graphql/mutationError'

/**
 * design.md §6.5.1: archive is reversible and touches no pages. The
 * `archivedSpaces` listing is scoped server-side to exactly who
 * `restoreSpace` accepts (instance admin, or that space's own
 * space-admin), so every row shown here is restorable by the caller — and
 * an empty list means "nothing archived that *you* could restore", which
 * is what the empty state says.
 */
export function ArchivedSpacesPage() {
  useDocumentTitle('Archived spaces')
  const [{ data, fetching, error }, refetch] = useArchivedSpacesQuery()
  const [, restoreSpace] = useRestoreSpaceMutation()
  const [restoringId, setRestoringId] = useState<string | null>(null)
  // Split in two, deliberately. One `severity`-carrying state cannot take the
  // right surface for both outcomes: a success is transient and belongs in an
  // auto-hiding Snackbar, while a refusal has to stay until it is read
  // (web/README.md's feedback rule). Sharing one slot is what kept this screen
  // out of the convergence the other six screens made.
  const [notice, setNotice] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)

  const handleRestore = async (spaceId: string, name: string) => {
    setRestoringId(spaceId)
    setActionError(null)
    const result = await restoreSpace({ input: { spaceId } })
    setRestoringId(null)
    // A transport failure carries no payload, so the typed-error helper returns
    // null and the button would appear to do nothing — the same hole the create
    // and restore actions had.
    if (result.error !== undefined) {
      setActionError(describeWriteFailure('RESTORE_SPACE').summary)
      return
    }
    const payload = result.data?.restoreSpace
    const errorText = describeMutationError(payload?.error)
    if (errorText) {
      setActionError(errorText)
      return
    }
    if (payload?.space) {
      setNotice(`Restored "${name}".`)
      refetch({ requestPolicy: 'network-only' })
    }
  }

  return (
    <Stack spacing={2}>
      <PageHeader title="Archived spaces" subject={{ label: 'Spaces', to: '/' }} />

      {actionError && (
        <Alert severity="warning" onClose={() => setActionError(null)}>
          {actionError}
        </Alert>
      )}

      {fetching && <Skeleton variant="rectangular" height={200} />}

      {!fetching && error && <Alert severity="info">{describeLoadFailure('ARCHIVED_SPACES').summary}</Alert>}

      {!fetching && !error && (data?.archivedSpaces.length ?? 0) === 0 && (
        <Typography color="text.secondary">No archived spaces you can restore.</Typography>
      )}

      {!fetching && !error && (data?.archivedSpaces.length ?? 0) > 0 && (
        <List>
          {data?.archivedSpaces.map((space) => (
            <ListItem
              key={space.id}
              divider
              secondaryAction={
                <Button
                  size="small"
                  disabled={restoringId === space.id}
                  onClick={() => void handleRestore(space.id, space.name)}
                >
                  Restore
                </Button>
              }
            >
              <ListItemText
                primary={`${space.name} (${space.key})`}
                secondary={`Archived ${formatTimestamp(space.archivedAtUtc)}`}
              />
            </ListItem>
          ))}
        </List>
      )}

      {/* Success is transient and requires no action, so it takes the Snackbar
          — web/README.md's rule, and the surface the other six screens already
          converged on. It was stuck in an inline Alert only because one state
          held refusals too. */}
      <Snackbar open={notice !== null} autoHideDuration={SNACKBAR_AUTO_HIDE_MS} onClose={() => setNotice(null)}>
        <Alert severity="success" onClose={() => setNotice(null)}>
          {notice}
        </Alert>
      </Snackbar>
    </Stack>
  )
}
