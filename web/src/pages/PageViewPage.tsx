import { useMemo, useState } from 'react'
import { useParams, useNavigate, Link as RouterLink } from 'react-router-dom'
import { Alert, Box, Button, Chip, Divider, Skeleton, Stack, Typography } from '@mui/material'
import EditOutlinedIcon from '@mui/icons-material/EditOutlined'
import DriveFileMoveOutlinedIcon from '@mui/icons-material/DriveFileMoveOutlined'
import LocalOfferOutlinedIcon from '@mui/icons-material/LocalOfferOutlined'
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined'
import NotificationsNoneOutlinedIcon from '@mui/icons-material/NotificationsNoneOutlined'
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive'
import {
  usePageByIdQuery,
  useSpaceTreeForMoveQuery,
  useMovePageMutation,
  useAddCommentMutation,
  useDeleteCommentMutation,
  useDeletePageMutation,
  useWatchPageMutation,
  useUnwatchPageMutation,
} from '../graphql/generated/graphql'
import { asReadOnlyReplica, blockedPageCount, describeMutationError } from '../graphql/mutationError'
import { RichTextEditor } from '../editor/RichTextEditor'
import { MovePageDialog } from './MovePageDialog'
import { DeletePageDialog } from './DeletePageDialog'
import { ReadOnlyReplicaDialog } from './ReadOnlyReplicaDialog'
import { flattenMoveTargets, nextSortOrderByTarget } from '../access/move/flattenMoveTargets'
import { AttachmentList } from '../attachments/AttachmentList'
import { AttachmentUploadButton } from '../attachments/AttachmentUploadButton'
import { Comments } from '../comments/Comments'
import { useScrollToHash } from './useScrollToHash'
import { usePresence } from '../presence/usePresence'
import { PresenceAvatars } from '../presence/PresenceAvatars'
import { PresencePointers } from '../presence/PresencePointers'
import { getDefaultPresenceTransport } from '../realtime/transports'

/**
 * Page view. Renders through the exact same `RichTextEditor` component as
 * the edit route, just with `editable={false}` — design.md §4's "one
 * renderer" rule: there must never be a second Markdown-to-view pipeline
 * that can drift from what the editor round-trips.
 *
 * NOTE (schema reconciliation): the real Page type exposes no
 * viewer-permission fields (canEdit/canComment/canManageAccess) and no
 * restriction summary — reported as a contract gap. Until the API grows
 * them, edit affordances are offered to everyone who can *view* the page
 * and the server's typed refusals (Forbidden / ReadOnlyReplica) render as
 * designed UX rather than raw errors. The restriction lock-badge and the
 * per-user hiding this page used to do cannot be honored client-side
 * without that data. Label editing is likewise disabled: the schema
 * exposes label *names* only, and attach/detach need label ids there is
 * no read path for.
 */
export function PageViewPage() {
  const { pageId } = useParams<{ pageId: string }>()
  const navigate = useNavigate()
  const [{ data, fetching, error }, refetchPage] = usePageByIdQuery({ variables: { id: pageId ?? '' }, pause: !pageId })
  const [moveOpen, setMoveOpen] = useState(false)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [blockedCount, setBlockedCount] = useState<number | null>(null)
  const [replicaOrigin, setReplicaOrigin] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  // No read path for "am I watching this page?" (reported contract gap) —
  // null means unknown; the toggle reflects only what this visit did.
  const [watching, setWatching] = useState<boolean | null>(null)
  // Search results (design.md §9) link to `#anchorId` — the heading isn't
  // in the DOM yet at the moment of navigation (content loads and the
  // editor mounts asynchronously), so this watches for it to appear rather
  // than relying on the browser's one-shot native hash scroll.
  useScrollToHash(data?.page?.content)

  const spaceId = data?.page?.spaceId
  const [{ data: treeData }] = useSpaceTreeForMoveQuery({ variables: { spaceId: spaceId ?? '' }, pause: !spaceId })
  const [, movePage] = useMovePageMutation()
  const [, addComment] = useAddCommentMutation()
  const [, deleteComment] = useDeleteCommentMutation()
  const [, deletePage] = useDeletePageMutation()
  const [, watchPage] = useWatchPageMutation()
  const [, unwatchPage] = useUnwatchPageMutation()
  // Depends on pageId, not just mount — see usePresence.ts's comment on
  // why route reuse (same component, different pageId) needs this. The
  // transport is the app-lifetime singleton from realtime/transports.ts.
  const { viewers, pointers, recordPointer } = usePresence(pageId ?? '', getDefaultPresenceTransport())

  const targetOptions = useMemo(() => {
    if (!treeData?.pageTree || !pageId) return []
    return flattenMoveTargets(treeData.pageTree, pageId)
  }, [treeData, pageId])

  const sortOrders = useMemo(() => nextSortOrderByTarget(treeData?.pageTree ?? []), [treeData])

  /** Routes a mutation error to the right UX; returns true when there was one. */
  const surfaceError = (mutationError: Parameters<typeof asReadOnlyReplica>[0]): boolean => {
    const replica = asReadOnlyReplica(mutationError)
    if (replica) {
      setReplicaOrigin(replica.originInstanceId ?? 'its origin instance')
      return true
    }
    const text = describeMutationError(mutationError)
    if (text) {
      setActionError(text)
      return true
    }
    return false
  }

  const handleToggleWatch = async () => {
    if (!pageId) return
    setActionError(null)
    if (watching === true) {
      const result = await unwatchPage({ input: { pageId } })
      // design.md §8: unwatching is deliberately ungated — treat any
      // response as unwatched unless a typed error says otherwise.
      if (!surfaceError(result.data?.unwatchPage.error)) setWatching(false)
      return
    }
    const result = await watchPage({ input: { pageId } })
    if (!surfaceError(result.data?.watchPage.error)) setWatching(true)
  }

  if (fetching) {
    return (
      <Stack spacing={1}>
        <Skeleton variant="text" width="40%" height={48} />
        <Skeleton variant="rectangular" height={200} />
      </Stack>
    )
  }

  if (error || !data?.page) {
    // Same message for "doesn't exist", "not viewable" and "API down" —
    // design.md §6.7's absent-not-forbidden applies client-side too.
    return <Alert severity="info">Couldn't load this page.</Alert>
  }

  const page = data.page

  // Real Comment rows carry the author's user id only (no display-name
  // resolver yet — reported contract gap); the thread shows a stable,
  // non-sensitive stand-in derived from the id rather than nothing.
  const comments = page.comments.map((c) => ({
    id: c.id,
    parentCommentId: c.parentCommentId,
    body: c.body,
    isDeleted: c.isDeleted,
    authorUserId: c.authorUserId,
    authorDisplayName: `User ${c.authorUserId.slice(0, 8)}`,
    createdAtUtc: c.createdAtUtc,
    editedAtUtc: c.editedAtUtc,
  }))

  return (
    <Box>
      <Stack direction="row" sx={{ mb: 2, alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="h4" component="h1">
          {page.title}
        </Typography>
        <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
          <PresenceAvatars viewers={viewers} />
          <Stack direction="row" spacing={1}>
            <Button
              startIcon={watching === true ? <NotificationsActiveIcon /> : <NotificationsNoneOutlinedIcon />}
              variant="outlined"
              size="small"
              onClick={() => void handleToggleWatch()}
              aria-pressed={watching === true}
            >
              {watching === true ? 'Watching' : 'Watch'}
            </Button>
            <Button startIcon={<DriveFileMoveOutlinedIcon />} variant="outlined" size="small" onClick={() => setMoveOpen(true)}>
              Move
            </Button>
            <Button
              component={RouterLink}
              to={`/pages/${page.id}/edit`}
              startIcon={<EditOutlinedIcon />}
              variant="outlined"
              size="small"
            >
              Edit
            </Button>
            <Button
              startIcon={<DeleteOutlinedIcon />}
              variant="outlined"
              color="error"
              size="small"
              onClick={() => {
                setBlockedCount(null)
                setDeleteOpen(true)
              }}
            >
              Delete
            </Button>
          </Stack>
        </Stack>
      </Stack>

      {actionError && (
        <Alert severity="warning" sx={{ mb: 2 }} onClose={() => setActionError(null)}>
          {actionError}
        </Alert>
      )}

      {page.labels.length > 0 && (
        <Box sx={{ mb: 2 }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
            {page.labels.map((label) => (
              <Chip key={label} size="small" label={label} icon={<LocalOfferOutlinedIcon />} />
            ))}
          </Stack>
        </Box>
      )}

      <Box
        sx={{ position: 'relative' }}
        onMouseMove={(e) => {
          const rect = e.currentTarget.getBoundingClientRect()
          if (rect.width === 0 || rect.height === 0) return
          recordPointer((e.clientX - rect.left) / rect.width, (e.clientY - rect.top) / rect.height)
        }}
      >
        <RichTextEditor initialMarkdown={page.content} editable={false} showToolbar={false} />
        <PresencePointers pointers={pointers} />
      </Box>

      <Box sx={{ mt: 2 }}>
        <AttachmentUploadButton pageId={page.id} onUploaded={() => refetchPage({ requestPolicy: 'network-only' })} />
      </Box>
      <Box sx={{ mt: 1 }}>
        <AttachmentList attachments={page.attachments} />
      </Box>

      <Divider sx={{ my: 4 }} />

      <Comments
        pageId={page.id}
        comments={comments}
        canComment
        // No local-user-id read path and no canManageAccess field (reported
        // contract gaps): "may I delete this comment?" is unknowable, so the
        // affordance is hidden rather than offered-and-refused.
        currentUserId={undefined}
        canManageAccess={false}
        onAdd={async (body, parentCommentId) => {
          const result = await addComment({ input: { pageId: page.id, body, parentCommentId } })
          if (!surfaceError(result.data?.addComment.error)) {
            refetchPage({ requestPolicy: 'network-only' })
          }
        }}
        onDelete={async (commentId) => {
          const result = await deleteComment({ input: { commentId } })
          if (!surfaceError(result.data?.deleteComment.error)) {
            refetchPage({ requestPolicy: 'network-only' })
          }
        }}
      />

      <MovePageDialog
        open={moveOpen}
        onClose={() => setMoveOpen(false)}
        pageTitle={page.title}
        currentAncestorRestrictions={[]}
        targetOptions={targetOptions}
        restrictionDataUnavailable
        onConfirm={(newParentId) => {
          setMoveOpen(false)
          void movePage({
            input: { pageId: page.id, newParentPageId: newParentId, newSortOrder: sortOrders.get(newParentId) ?? 0 },
          }).then((result) => {
            if (!surfaceError(result.data?.movePage.error)) {
              refetchPage({ requestPolicy: 'network-only' })
            }
          })
        }}
      />

      <DeletePageDialog
        open={deleteOpen}
        onClose={() => setDeleteOpen(false)}
        pageTitle={page.title}
        hasChildren={page.children.length > 0}
        deleting={deleting}
        blockedDescendantCount={blockedCount}
        onConfirm={async () => {
          setDeleting(true)
          const result = await deletePage({ input: { pageId: page.id } })
          setDeleting(false)
          const payload = result.data?.deletePage
          const blocked = blockedPageCount(payload?.error)
          if (blocked !== null) {
            // Whole cascade refused — nothing was deleted (design.md
            // §6.4.1's "one operation"). Keep the dialog open so the
            // count is visible instead of silently closing on a no-op.
            setBlockedCount(blocked)
            return
          }
          if (surfaceError(payload?.error)) {
            setDeleteOpen(false)
            return
          }
          if (payload?.summary) {
            setDeleteOpen(false)
            navigate(page.parent ? `/pages/${page.parent.id}` : `/spaces/${page.spaceKey}`)
          }
        }}
      />

      {/* One replica dialog for every write on this page (move, delete,
          watch, comments) — design.md §12: explain the mirror, never a raw
          error toast. */}
      <ReadOnlyReplicaDialog
        open={replicaOrigin !== null}
        originInstanceId={replicaOrigin}
        onClose={() => setReplicaOrigin(null)}
      />
    </Box>
  )
}
