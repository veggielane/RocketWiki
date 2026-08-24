import { useState } from 'react'
import { Alert, Button, List, ListItem, ListItemText, Skeleton, Stack, Typography } from '@mui/material'
import { useArchivedSpacesQuery, useRestoreSpaceMutation } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
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
  const [{ data, fetching, error }, refetch] = useArchivedSpacesQuery()
  const [, restoreSpace] = useRestoreSpaceMutation()
  const [restoringId, setRestoringId] = useState<string | null>(null)
  const [message, setMessage] = useState<{ severity: 'success' | 'warning'; text: string } | null>(null)

  const handleRestore = async (spaceId: string, name: string) => {
    setRestoringId(spaceId)
    setMessage(null)
    const result = await restoreSpace({ input: { spaceId } })
    setRestoringId(null)
    const payload = result.data?.restoreSpace
    const errorText = describeMutationError(payload?.error)
    if (errorText) {
      setMessage({ severity: 'warning', text: errorText })
      return
    }
    if (payload?.space) {
      setMessage({ severity: 'success', text: `Restored "${name}".` })
      refetch({ requestPolicy: 'network-only' })
    }
  }

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">
        Archived spaces
      </Typography>

      {message && <Alert severity={message.severity}>{message.text}</Alert>}

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
    </Stack>
  )
}
