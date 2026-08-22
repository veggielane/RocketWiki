import { useParams } from 'react-router-dom'
import { useState } from 'react'
import { Alert, Button, List, ListItem, ListItemText, Skeleton, Stack, Typography } from '@mui/material'
import { useRestoreTrashBatchMutation, useSpaceTrashQuery } from '../graphql/generated/graphql'
import { describeExpiry } from '../trash/trashCountdown'

/**
 * design.md §6.4.1: a cascade delete trashes a whole subtree as one
 * operation, so this lists **batches**, not one row per page — a subtree
 * of 12 pages shows as one entry, and restoring it brings all 12 back
 * together rather than requiring 12 separate restores.
 */
export function TrashPage() {
  const { spaceKey } = useParams<{ spaceKey: string }>()
  const [{ data, fetching, error }, refetch] = useSpaceTrashQuery({
    variables: { key: spaceKey ?? '' },
    pause: !spaceKey,
  })
  const [, restoreTrashBatch] = useRestoreTrashBatchMutation()
  const [restoringId, setRestoringId] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  if (!spaceKey) return null

  const handleRestore = async (batchId: string) => {
    setRestoringId(batchId)
    setMessage(null)
    const result = await restoreTrashBatch({ input: { batchId } })
    setRestoringId(null)
    const payload = result.data?.restoreTrashBatch
    if (payload?.readOnlyReplicaError) {
      setMessage(payload.readOnlyReplicaError.message)
      return
    }
    if (payload) {
      setMessage(`Restored ${payload.restoredPageCount} page${payload.restoredPageCount === 1 ? '' : 's'}.`)
      refetch({ requestPolicy: 'network-only' })
    }
  }

  if (fetching) {
    return <Skeleton variant="rectangular" height={300} />
  }

  if (error || !data?.space) {
    return <Alert severity="info">Couldn't load trash — there's no live API in this environment yet.</Alert>
  }

  const trash = data.space.trash

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">
        Trash: {data.space.name}
      </Typography>

      {message && <Alert severity="success">{message}</Alert>}

      {trash.length === 0 ? (
        <Typography color="text.secondary">Trash is empty.</Typography>
      ) : (
        <List>
          {trash.map((batch) => (
            <ListItem
              key={batch.id}
              divider
              secondaryAction={
                <Button size="small" disabled={restoringId === batch.id} onClick={() => void handleRestore(batch.id)}>
                  Restore
                </Button>
              }
            >
              <ListItemText
                primary={
                  batch.pageCount > 1
                    ? `${batch.rootPageTitle} + ${batch.pageCount - 1} more page${batch.pageCount - 1 === 1 ? '' : 's'}`
                    : batch.rootPageTitle
                }
                secondary={`Deleted by ${batch.deletedByDisplayName} · ${describeExpiry(batch.expiresAtUtc)}`}
              />
            </ListItem>
          ))}
        </List>
      )}
    </Stack>
  )
}
