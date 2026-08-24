import { useMemo, useState } from 'react'
import { useParams, useNavigate, Link as RouterLink } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  IconButton,
  Skeleton,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import EditOutlinedIcon from '@mui/icons-material/EditOutlined'
import DriveFileMoveOutlinedIcon from '@mui/icons-material/DriveFileMoveOutlined'
import LocalOfferOutlinedIcon from '@mui/icons-material/LocalOfferOutlined'
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined'
import NotificationsNoneOutlinedIcon from '@mui/icons-material/NotificationsNoneOutlined'
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive'
import PolicyOutlinedIcon from '@mui/icons-material/PolicyOutlined'
import ShieldOutlinedIcon from '@mui/icons-material/ShieldOutlined'
import {
  usePageByIdQuery,
  useCurrentUserQuery,
  useSpaceReplicaBannerQuery,
  useSpaceLabelDetailsQuery,
  useSpaceTreeForMoveQuery,
  useMovePageMutation,
  useAddCommentMutation,
  useDeleteCommentMutation,
  useDeletePageMutation,
  useWatchPageMutation,
  useUnwatchPageMutation,
  useCreateLabelMutation,
  useAttachLabelMutation,
  useDetachLabelMutation,
} from '../graphql/generated/graphql'
import { asReadOnlyReplica, blockedPageCount, describeMutationError } from '../graphql/mutationError'
import { describeLoadFailure, REPLICA_EXPLANATION, replicaBadgeLabel } from '../feedback/unavailableCopy'
import { RichTextEditor } from '../editor/RichTextEditor'
import { MovePageDialog } from '../access/move/MovePageDialog'
import { DeletePageDialog } from '../trash/DeletePageDialog'
import { ReadOnlyReplicaDialog } from '../feedback/ReadOnlyReplicaDialog'
import { ancestorRestrictionsOf, flattenMoveTargets, nextSortOrderByTarget } from '../access/move/flattenMoveTargets'
import { PermissionInspectorPanel } from '../access/permission/PermissionInspectorPanel'
import { AttachmentList } from '../attachments/AttachmentList'
import { AttachmentUploadButton } from '../attachments/AttachmentUploadButton'
import { Comments } from '../comments/Comments'
import { LabelEditor } from '../labels/LabelEditor'
import { computeLabelOps } from '../labels/labelOps'
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
 * Permissions shape the UI (design.md §6): the server-computed, replica-
 * aware `canEdit`/`canComment`/`canManageAccess` fields gate the
 * edit/move/delete, comment, and permissions affordances — hidden when the
 * server would refuse, never offered-and-refused. The server remains the
 * enforcement point; the typed refusals (Forbidden / ReadOnlyReplica /
 * StaleRevision) still render as designed UX if they ever arrive.
 */
export function PageViewPage() {
  const { pageId } = useParams<{ pageId: string }>()
  const navigate = useNavigate()
  const [{ data, fetching, error }, refetchPage] = usePageByIdQuery({ variables: { id: pageId ?? '' }, pause: !pageId })
  const [{ data: meData }] = useCurrentUserQuery()
  const [moveOpen, setMoveOpen] = useState(false)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [blockedCount, setBlockedCount] = useState<number | null>(null)
  const [replicaOrigin, setReplicaOrigin] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [editingLabels, setEditingLabels] = useState(false)
  const [inspectorOpen, setInspectorOpen] = useState(false)
  // Server truth (`viewerIsWatching`) with an optimistic local override:
  // the override is set on click, reverted if the mutation is refused, and
  // cleared when navigating to a different page.
  const [watchOverride, setWatchOverride] = useState<boolean | null>(null)
  // Route reuse (same component, different pageId) must not carry per-page
  // UI state across — same render-time reset idiom as AuditLogPage's
  // filter-key comparison.
  const [statePageId, setStatePageId] = useState(pageId)
  if (statePageId !== pageId) {
    setStatePageId(pageId)
    setWatchOverride(null)
    setEditingLabels(false)
    setInspectorOpen(false)
  }
  // Search results (design.md §9) link to `#anchorId` — the heading isn't
  // in the DOM yet at the moment of navigation (content loads and the
  // editor mounts asynchronously), so this watches for it to appear rather
  // than relying on the browser's one-shot native hash scroll.
  useScrollToHash(data?.page?.content)

  const spaceId = data?.page?.spaceId
  const spaceKey = data?.page?.spaceKey
  const canEdit = data?.page?.canEdit === true
  // The move dialog needs the tree (with restriction markers) only when
  // this user can actually move the page.
  const [{ data: treeData }] = useSpaceTreeForMoveQuery({
    variables: { spaceId: spaceId ?? '' },
    pause: !spaceId || !canEdit,
  })
  // design.md §12: proactive "Replica of {origin} — read-only" banner.
  const [{ data: spaceMeta }] = useSpaceReplicaBannerQuery({ variables: { key: spaceKey ?? '' }, pause: !spaceKey })
  // Label-id vocabulary for the editor — only fetched while editing.
  const [{ data: labelData }] = useSpaceLabelDetailsQuery({
    variables: { spaceKey: spaceKey ?? '' },
    pause: !spaceKey || !editingLabels,
  })
  const [, movePage] = useMovePageMutation()
  const [, addComment] = useAddCommentMutation()
  const [, deleteComment] = useDeleteCommentMutation()
  const [, deletePage] = useDeletePageMutation()
  const [, watchPage] = useWatchPageMutation()
  const [, unwatchPage] = useUnwatchPageMutation()
  const [, createLabel] = useCreateLabelMutation()
  const [, attachLabel] = useAttachLabelMutation()
  const [, detachLabel] = useDetachLabelMutation()
  // Depends on pageId, not just mount — see usePresence.ts's comment on
  // why route reuse (same component, different pageId) needs this. The
  // transport is the app-lifetime singleton from realtime/transports.ts.
  const { viewers, pointers, recordPointer } = usePresence(pageId ?? '', getDefaultPresenceTransport())

  const targetOptions = useMemo(() => {
    if (!treeData?.pageTree || !pageId) return []
    return flattenMoveTargets(treeData.pageTree, pageId)
  }, [treeData, pageId])

  // The "before" side of the move dialog's visibility warning (design.md
  // §6.4): what this page currently inherits from its ancestor chain.
  const currentAncestorRestrictions = useMemo(() => {
    if (!treeData?.pageTree || !pageId) return []
    return ancestorRestrictionsOf(treeData.pageTree, pageId)
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

  const watching = watchOverride ?? data?.page?.viewerIsWatching ?? false

  const handleToggleWatch = async () => {
    if (!pageId) return
    setActionError(null)
    const next = !watching
    setWatchOverride(next) // optimistic — reverted below if refused
    if (next) {
      const result = await watchPage({ input: { pageId } })
      if (result.error !== undefined || surfaceError(result.data?.watchPage.error)) setWatchOverride(!next)
      return
    }
    // design.md §8: unwatching is deliberately ungated — treat any
    // response as unwatched unless a typed error says otherwise.
    const result = await unwatchPage({ input: { pageId } })
    if (result.error !== undefined || surfaceError(result.data?.unwatchPage.error)) setWatchOverride(!next)
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
    return <Alert severity="info">{describeLoadFailure('PAGE').summary}</Alert>
  }

  const page = data.page
  const replicaSpace = spaceMeta?.space?.isReplica === true ? spaceMeta.space : null

  const comments = page.comments.map((c) => ({
    id: c.id,
    parentCommentId: c.parentCommentId,
    body: c.body,
    isDeleted: c.isDeleted,
    authorUserId: c.authorUserId,
    authorDisplayName: c.author.displayName,
    authorHasAvatar: c.author.hasAvatar,
    createdAtUtc: c.createdAtUtc,
    editedAtUtc: c.editedAtUtc,
  }))

  const attachments = page.attachments.map((a) => ({
    id: a.id,
    fileName: a.fileName,
    contentType: a.contentType,
    sizeBytes: a.sizeBytes,
    uploadedById: a.uploadedBy.id,
    uploadedByDisplayName: a.uploadedBy.displayName,
    uploadedByHasAvatar: a.uploadedBy.hasAvatar,
  }))

  const saveLabels = async (names: string[]) => {
    setActionError(null)
    const ops = computeLabelOps(
      page.labelDetails.map((label) => ({ id: label.id, name: label.name })),
      names,
      (labelData?.labelDetails ?? []).map((label) => ({ id: label.id, name: label.name })),
    )
    for (const name of ops.create) {
      const created = await createLabel({ input: { spaceId: page.spaceId, name } })
      if (surfaceError(created.data?.createLabel.error)) return
      const label = created.data?.createLabel.label
      if (!label) return
      const attached = await attachLabel({ input: { pageId: page.id, labelId: label.id } })
      if (surfaceError(attached.data?.attachLabel.error)) return
    }
    for (const label of ops.attach) {
      const attached = await attachLabel({ input: { pageId: page.id, labelId: label.id } })
      if (surfaceError(attached.data?.attachLabel.error)) return
    }
    for (const label of ops.detach) {
      const detached = await detachLabel({ input: { pageId: page.id, labelId: label.id } })
      if (surfaceError(detached.data?.detachLabel.error)) return
    }
    setEditingLabels(false)
    refetchPage({ requestPolicy: 'network-only' })
  }

  return (
    <Box>
      <Stack direction="row" sx={{ mb: 2, alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="h4" component="h1">
          {page.title}
        </Typography>
        <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
          <PresenceAvatars viewers={viewers} />
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <Tooltip title="Why can I see this page?">
              <IconButton size="small" aria-label="Why can I see this page?" onClick={() => setInspectorOpen(true)}>
                <PolicyOutlinedIcon fontSize="small" />
              </IconButton>
            </Tooltip>
            <Button
              startIcon={watching ? <NotificationsActiveIcon /> : <NotificationsNoneOutlinedIcon />}
              variant="outlined"
              size="small"
              onClick={() => void handleToggleWatch()}
              aria-pressed={watching}
            >
              {watching ? 'Watching' : 'Watch'}
            </Button>
            {/* design.md §6.4.1: move requires canEdit at the source (and
                the server re-checks the destination). */}
            {page.canEdit && (
              <Button
                startIcon={<DriveFileMoveOutlinedIcon />}
                variant="outlined"
                size="small"
                onClick={() => setMoveOpen(true)}
              >
                Move
              </Button>
            )}
            {page.canEdit && (
              <Button
                component={RouterLink}
                to={`/pages/${page.id}/edit`}
                startIcon={<EditOutlinedIcon />}
                variant="outlined"
                size="small"
              >
                Edit
              </Button>
            )}
            {page.canManageAccess && (
              <Button
                component={RouterLink}
                to={`/pages/${page.id}/permissions`}
                startIcon={<ShieldOutlinedIcon />}
                variant="outlined"
                size="small"
              >
                Permissions
              </Button>
            )}
            {page.canEdit && (
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
            )}
          </Stack>
        </Stack>
      </Stack>

      {replicaSpace && (
        <Alert severity="info" sx={{ mb: 2 }}>
          {replicaBadgeLabel(replicaSpace.originInstanceId)}. {REPLICA_EXPLANATION}
        </Alert>
      )}

      {actionError && (
        <Alert severity="warning" sx={{ mb: 2 }} onClose={() => setActionError(null)}>
          {actionError}
        </Alert>
      )}

      {(page.labels.length > 0 || page.canEdit) && (
        <Box sx={{ mb: 2 }}>
          {editingLabels ? (
            <LabelEditor
              labels={page.labelDetails.map((label) => label.name)}
              knownLabels={(labelData?.labelDetails ?? []).map((label) => label.name)}
              onSave={saveLabels}
              onCancel={() => setEditingLabels(false)}
            />
          ) : (
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
              {page.labels.map((label) => (
                <Chip key={label} size="small" label={label} icon={<LocalOfferOutlinedIcon />} />
              ))}
              {/* design.md §6.4.2: attaching/detaching labels requires canEdit on this page. */}
              {page.canEdit && (
                <Button size="small" startIcon={<LocalOfferOutlinedIcon />} onClick={() => setEditingLabels(true)}>
                  {page.labels.length > 0 ? 'Edit labels' : 'Add labels'}
                </Button>
              )}
            </Stack>
          )}
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

      {page.canEdit && (
        <Box sx={{ mt: 2 }}>
          <AttachmentUploadButton pageId={page.id} onUploaded={() => refetchPage({ requestPolicy: 'network-only' })} />
        </Box>
      )}
      <Box sx={{ mt: 1 }}>
        <AttachmentList attachments={attachments} />
      </Box>

      <Divider sx={{ my: 4 }} />

      <Comments
        pageId={page.id}
        comments={comments}
        canComment={page.canComment}
        // Delete-own matches Comment.authorUserId against the local User
        // row id (CurrentUser.localUserId); moderation deletes ride on
        // canManageAccess (design.md §6.4.2).
        currentUserId={meData?.me.localUserId ?? undefined}
        canManageAccess={page.canManageAccess}
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
        currentAncestorRestrictions={currentAncestorRestrictions}
        targetOptions={targetOptions}
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

      {/* Self-inspection for any viewer (design.md §6.6) — mounted only
          while open, so merely viewing a page never fires the audited
          permission.inspect query. */}
      <Dialog open={inspectorOpen} onClose={() => setInspectorOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle>Why can I see this page?</DialogTitle>
        <DialogContent>{inspectorOpen && <PermissionInspectorPanel pageId={page.id} />}</DialogContent>
        <DialogActions>
          <Button onClick={() => setInspectorOpen(false)}>Close</Button>
        </DialogActions>
      </Dialog>

      {/* One replica dialog for every write on this page (move, delete,
          watch, comments) — design.md §12: explain the replica, never a raw
          error toast. */}
      <ReadOnlyReplicaDialog
        open={replicaOrigin !== null}
        originInstanceId={replicaOrigin}
        onClose={() => setReplicaOrigin(null)}
      />
    </Box>
  )
}
