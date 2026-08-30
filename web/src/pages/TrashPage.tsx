import { useParams } from 'react-router-dom'
import { useMemo, useState } from 'react'
import { Alert, Button, List, ListItem, ListItemText, Skeleton, Snackbar, Stack, Typography } from '@mui/material'
import { useRestorePageMutation, useSpaceTrashQuery } from '../graphql/generated/graphql'
import { asReadOnlyReplica, describeMutationError } from '../graphql/mutationError'
import { describeLoadFailure, describeWriteFailure, REPLICA_EXPLANATION, replicaBadgeLabel } from '../feedback/unavailableCopy'
import { SNACKBAR_AUTO_HIDE_MS } from '../feedback/snackbar'
import { groupTrashBatches } from '../trash/groupTrashBatches'
import { describeExpiry } from '../trash/trashCountdown'
import { UserAvatar } from '../avatars/UserAvatar'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'

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
  // Split in two, deliberately. One `severity`-carrying state cannot take the
  // right surface for both outcomes: a success is transient and belongs in an
  // auto-hiding Snackbar, while a refusal has to stay until it is read
  // (web/README.md's feedback rule). Sharing one slot is what kept this screen
  // out of the convergence the other six screens made.
  const [notice, setNotice] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)

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

  useDocumentTitle(data?.space ? `Trash — ${data.space.name}` : 'Trash')

  if (!spaceKey) return null

  const handleRestore = async (batchId: string, rootPageId: string) => {
    setRestoringId(batchId)
    setActionError(null)
    const result = await restorePage({ input: { pageId: rootPageId } })
    setRestoringId(null)
    // Same silent-failure hole as the create-space button: with no `data`,
    // `describeMutationError` returns null and Restore appeared to do nothing.
    if (result.error !== undefined) {
      setActionError(describeWriteFailure('RESTORE_PAGE').summary)
      return
    }
    const payload = result.data?.restorePage
    const replica = asReadOnlyReplica(payload?.error)
    if (replica) {
      setActionError(`${replicaBadgeLabel(replica.originInstanceId)}. ${REPLICA_EXPLANATION}`)
      return
    }
    const errorText = describeMutationError(payload?.error)
    if (errorText) {
      setActionError(errorText)
      return
    }
    if (payload?.summary) {
      const count = payload.summary.restoredPageCount
      setNotice(`Restored ${count} page${count === 1 ? '' : 's'}.`)
      refetch({ requestPolicy: 'network-only' })
    }
  }

  // FIRST LOAD ONLY. urql retains `data` across a refetch and flips `fetching`
  // true (urql.js computeNextState), so a bare `if (fetching)` threw the screen
  // away on every post-write refetch: content, scroll position and keyboard
  // focus all went with it. `&& !data` keeps the rendered screen up while the
  // re-read happens underneath it.
  const loadingFirstTime = fetching && !data
  const space = data?.space ?? null

  return (
    <Stack spacing={2}>
      {/* The heading renders on every branch. Both the loading skeleton and the
          failure Alert used to REPLACE the whole screen, taking the only <h1>
          with them — so the two states a first-time visitor is most likely to
          meet were the two with no heading to land on. The space name joins it
          when the read succeeds. */}
      <PageHeader
        title="Trash"
        subject={space ? { label: space.name, to: `/spaces/${space.key}` } : undefined}
      />

      {loadingFirstTime && <Skeleton variant="rectangular" height={300} />}

      {!loadingFirstTime && (error || !space) && (
        <Alert severity="info">{describeLoadFailure('TRASH').summary}</Alert>
      )}

      {actionError && (
        <Alert severity="warning" onClose={() => setActionError(null)}>
          {actionError}
        </Alert>
      )}

      {/* Neither the sentence nor the list belongs to a screen that has not
          loaded or has failed — an empty <List> under an error Alert is the
          shape of a successful, empty read. */}
      {!loadingFirstTime && space && (batches.length === 0 ? (
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
      ))}

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
