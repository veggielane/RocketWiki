import { useParams } from 'react-router-dom'
import { useMemo, useState } from 'react'
import { Alert, Button, List, ListItem, ListItemText, Skeleton, Stack, Typography } from '@mui/material'
import { useRestorePageMutation, useSpaceTrashQuery } from '../graphql/generated/graphql'
import { asReadOnlyReplica, describeMutationError } from '../graphql/mutationError'
import { describeLoadFailure, REPLICA_EXPLANATION, replicaBadgeLabel } from '../feedback/unavailableCopy'
import { groupTrashBatches } from '../trash/groupTrashBatches'
import { describeExpiry } from '../trash/trashCountdown'
import { UserAvatar } from '../avatars/UserAvatar'

/**
 * design.md §6.4.1: a cascade delete trashes a whole subtree as one
 * operation, so this lists **batches**, not one row per page — a subtree
 * of 12 pages shows as one entry, and restoring it brings all 12 back
 * together rather than requiring 12 separate restores. The real schema
 * sends trashed pages with a `deleteBatchId` stamp; grouping happens
 * client-side (trash/groupTrashBatches.ts), and restore goes through
 * `restorePage` with the batch's root page id.
 */
export function TrashPage() {
  const { spaceKey } = useParams<{ spaceKey: string }>()
  const [{ data, fetching, error }, refetch] = useSpaceTrashQuery({
    variables: { key: spaceKey ?? '' },
    pause: !spaceKey,
  })
  const [, restorePage] = useRestorePageMutation()
  const [restoringId, setRestoringId] = useState<string | null>(null)
  const [message, setMessage] = useState<{ severity: 'success' | 'warning'; text: string } | null>(null)

  const batches = useMemo(
    () =>
      groupTrashBatches(
        (data?.space?.trashedPages ?? []).map((page) => ({
          ...page,
          deletedByDisplayName: page.deletedBy?.displayName ?? null,
          deletedById: page.deletedBy?.id ?? null,
          deletedByHasAvatar: page.deletedBy?.hasAvatar ?? null,
        })),
      ),
    [data],
  )

  if (!spaceKey) return null

  const handleRestore = async (batchId: string, rootPageId: string) => {
    setRestoringId(batchId)
    setMessage(null)
    const result = await restorePage({ input: { pageId: rootPageId } })
    setRestoringId(null)
    const payload = result.data?.restorePage
    const replica = asReadOnlyReplica(payload?.error)
    if (replica) {
      setMessage({
        severity: 'warning',
        text: `${replicaBadgeLabel(replica.originInstanceId)}. ${REPLICA_EXPLANATION}`,
      })
      return
    }
    const errorText = describeMutationError(payload?.error)
    if (errorText) {
      setMessage({ severity: 'warning', text: errorText })
      return
    }
    if (payload?.summary) {
      const count = payload.summary.restoredPageCount
      setMessage({ severity: 'success', text: `Restored ${count} page${count === 1 ? '' : 's'}.` })
      refetch({ requestPolicy: 'network-only' })
    }
  }

  if (fetching) {
    return <Skeleton variant="rectangular" height={300} />
  }

  if (error || !data?.space) {
    return <Alert severity="info">{describeLoadFailure('TRASH').summary}</Alert>
  }

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">
        Trash: {data.space.name}
      </Typography>

      {message && <Alert severity={message.severity}>{message.text}</Alert>}

      {batches.length === 0 ? (
        <Typography color="text.secondary">
          Trash is empty — deleted pages land here and can be restored for 30 days.
        </Typography>
      ) : (
        <List>
          {batches.map((batch) => (
            <ListItem
              key={batch.id}
              divider
              secondaryAction={
                <Button
                  size="small"
                  disabled={restoringId === batch.id}
                  onClick={() => void handleRestore(batch.id, batch.rootPageId)}
                >
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
                // The secondary line can hold an Avatar (a div) — rendered
                // as a div, not the default <p>, to keep the markup valid.
                slotProps={{ secondary: { component: 'div' } }}
                secondary={
                  batch.deletedByDisplayName || batch.expiresAtUtc ? (
                    <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
                      {batch.deletedByDisplayName && (
                        <>
                          <span>Deleted by</span>
                          <UserAvatar
                            userId={batch.deletedById}
                            hasAvatar={batch.deletedByHasAvatar ?? false}
                            displayName={batch.deletedByDisplayName}
                            size={16}
                          />
                          <span>{batch.deletedByDisplayName}</span>
                        </>
                      )}
                      {batch.expiresAtUtc && (
                        <span>
                          {batch.deletedByDisplayName ? ' · ' : ''}
                          {describeExpiry(batch.expiresAtUtc)}
                        </span>
                      )}
                    </Stack>
                  ) : undefined
                }
              />
            </ListItem>
          ))}
        </List>
      )}
    </Stack>
  )
}
