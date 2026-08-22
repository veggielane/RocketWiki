import { useMemo, useState } from 'react'
import { useParams, useNavigate, Link as RouterLink } from 'react-router-dom'
import { Alert, Box, Button, Chip, Divider, Skeleton, Stack, Typography } from '@mui/material'
import LockOutlinedIcon from '@mui/icons-material/LockOutlined'
import EditOutlinedIcon from '@mui/icons-material/EditOutlined'
import ShieldOutlinedIcon from '@mui/icons-material/ShieldOutlined'
import DriveFileMoveOutlinedIcon from '@mui/icons-material/DriveFileMoveOutlined'
import LocalOfferOutlinedIcon from '@mui/icons-material/LocalOfferOutlined'
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined'
import {
  usePageByIdQuery,
  useSpaceTreeForMoveQuery,
  useMovePageMutation,
  useSetLabelsMutation,
  useAddCommentMutation,
  useDeleteCommentMutation,
  useCurrentUserQuery,
  useDeletePageMutation,
} from '../graphql/generated/graphql'
import { RichTextEditor } from '../editor/RichTextEditor'
import { MovePageDialog } from './MovePageDialog'
import { DeletePageDialog } from './DeletePageDialog'
import { flattenMoveTargets } from '../access/move/flattenMoveTargets'
import { LabelEditor } from '../labels/LabelEditor'
import { AttachmentList } from '../attachments/AttachmentList'
import { AttachmentUploadButton } from '../attachments/AttachmentUploadButton'
import { Comments } from '../comments/Comments'
import { useScrollToHash } from './useScrollToHash'
import { usePresence } from '../presence/usePresence'
import { PresenceAvatars } from '../presence/PresenceAvatars'
import { PresencePointers } from '../presence/PresencePointers'
import { FakePresenceTransport } from '../realtime/FakePresenceTransport'

// One transport instance for the app's lifetime, same reasoning as
// NotificationBell's transport — recreating it per render would
// join/leave on every re-render. Swap for `SignalRPresenceTransport` once
// a presence hub exists (design.md §8, milestone 4b).
const presenceTransport = new FakePresenceTransport()

/**
 * Page view. Renders through the exact same `RichTextEditor` component as
 * the edit route, just with `editable={false}` — design.md §4's "one
 * renderer" rule: there must never be a second Markdown-to-view pipeline
 * that can drift from what the editor round-trips. Comments reuse it too.
 */
export function PageViewPage() {
  const { pageId } = useParams<{ pageId: string }>()
  const navigate = useNavigate()
  const [{ data, fetching, error }, refetchPage] = usePageByIdQuery({ variables: { id: pageId ?? '' }, pause: !pageId })
  const [moveOpen, setMoveOpen] = useState(false)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [blockedDescendantCount, setBlockedDescendantCount] = useState<number | null>(null)
  // Search results (design.md §9) link to `#anchorId` — the heading isn't
  // in the DOM yet at the moment of navigation (content loads and the
  // editor mounts asynchronously), so this watches for it to appear rather
  // than relying on the browser's one-shot native hash scroll.
  useScrollToHash(data?.page?.content)
  const [editingLabels, setEditingLabels] = useState(false)

  const spaceKey = data?.page?.spaceKey
  const [{ data: treeData }] = useSpaceTreeForMoveQuery({ variables: { key: spaceKey ?? '' }, pause: !spaceKey })
  const [, movePage] = useMovePageMutation()
  const [, setLabels] = useSetLabelsMutation()
  const [, addComment] = useAddCommentMutation()
  const [, deleteComment] = useDeleteCommentMutation()
  const [, deletePage] = useDeletePageMutation()
  const [{ data: meData }] = useCurrentUserQuery()
  // Depends on pageId, not just mount — see usePresence.ts's comment on
  // why route reuse (same component, different pageId) needs this.
  const { viewers, pointers, recordPointer } = usePresence(pageId ?? '', presenceTransport)

  const targetOptions = useMemo(() => {
    if (!treeData?.space || !pageId) return []
    return flattenMoveTargets(treeData.space.tree, pageId)
  }, [treeData, pageId])

  const currentAncestorRestrictions = useMemo(() => {
    const parentId = data?.page?.parent?.id
    if (!parentId) return []
    return targetOptions.find((t) => t.id === parentId)?.ancestorRestrictions ?? []
  }, [targetOptions, data])

  if (fetching) {
    return (
      <Stack spacing={1}>
        <Skeleton variant="text" width="40%" height={48} />
        <Skeleton variant="rectangular" height={200} />
      </Stack>
    )
  }

  if (error || !data?.page) {
    return (
      <Alert severity="info">
        Couldn't load this page — there's no live API in this environment yet.
      </Alert>
    )
  }

  const page = data.page

  return (
    <Box>
      <Stack direction="row" sx={{ mb: 2, alignItems: 'center', justifyContent: 'space-between' }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <Typography variant="h4" component="h1">
            {page.title}
          </Typography>
          {(page.restrictions.hasViewRestriction || page.restrictions.hasEditRestriction) && (
            <Chip
              icon={<LockOutlinedIcon />}
              label={page.restrictions.summary.join(', ') || 'Restricted'}
              size="small"
              color="warning"
              variant="outlined"
            />
          )}
        </Stack>
        <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
          <PresenceAvatars viewers={viewers} />
          <Stack direction="row" spacing={1}>
          {/* Hidden rather than disabled when unauthorized — the point is
              no dead-end affordance, not a visible-but-blocked one
              (design.md §6.7's "absent rather than forbidden"). */}
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
            <Button startIcon={<DriveFileMoveOutlinedIcon />} variant="outlined" size="small" onClick={() => setMoveOpen(true)}>
              Move
            </Button>
          )}
          {/* Edit affordance only offered when the server says canEdit — the
              server still enforces this on save regardless (design.md §6). */}
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
          {page.canEdit && (
            <Button
              startIcon={<DeleteOutlinedIcon />}
              variant="outlined"
              color="error"
              size="small"
              onClick={() => {
                setBlockedDescendantCount(null)
                setDeleteOpen(true)
              }}
            >
              Delete
            </Button>
          )}
          </Stack>
        </Stack>
      </Stack>

      <Box sx={{ mb: 2 }}>
        {editingLabels ? (
          <LabelEditor
            labels={page.labels}
            knownLabels={treeData?.space?.labels ?? []}
            onCancel={() => setEditingLabels(false)}
            onSave={async (labels) => {
              await setLabels({ input: { pageId: page.id, labels } })
              setEditingLabels(false)
            }}
          />
        ) : (
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
            {page.labels.map((label) => (
              <Chip key={label} size="small" label={label} icon={<LocalOfferOutlinedIcon />} />
            ))}
            {page.canEdit && (
              <Button size="small" onClick={() => setEditingLabels(true)}>
                {page.labels.length > 0 ? 'Edit labels' : 'Add labels'}
              </Button>
            )}
          </Stack>
        )}
      </Box>

      <Box
        sx={{ position: 'relative' }}
        onMouseMove={(e) => {
          const rect = e.currentTarget.getBoundingClientRect()
          if (rect.width === 0 || rect.height === 0) return
          recordPointer((e.clientX - rect.left) / rect.width, (e.clientY - rect.top) / rect.height)
        }}
      >
        <RichTextEditor initialMarkdown={page.content} editable={false} showToolbar={false} />
        <PresencePointers viewers={viewers} pointers={pointers} />
      </Box>

      {page.canEdit && (
        <Box sx={{ mt: 2 }}>
          <AttachmentUploadButton pageId={page.id} onUploaded={() => refetchPage({ requestPolicy: 'network-only' })} />
        </Box>
      )}
      <Box sx={{ mt: 1 }}>
        <AttachmentList attachments={page.attachments} />
      </Box>

      <Divider sx={{ my: 4 }} />

      <Comments
        pageId={page.id}
        comments={page.comments}
        canComment={page.canComment}
        currentUserId={meData?.me.id}
        canManageAccess={page.canManageAccess}
        onAdd={async (body, parentCommentId) => {
          await addComment({ input: { pageId: page.id, body, parentCommentId } })
          refetchPage({ requestPolicy: 'network-only' })
        }}
        onDelete={async (commentId) => {
          await deleteComment({ input: { commentId } })
          refetchPage({ requestPolicy: 'network-only' })
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
          void movePage({ input: { pageId: page.id, newParentId } })
        }}
      />

      <DeletePageDialog
        open={deleteOpen}
        onClose={() => setDeleteOpen(false)}
        pageTitle={page.title}
        hasChildren={page.children.length > 0}
        deleting={deleting}
        blockedDescendantCount={blockedDescendantCount}
        onConfirm={async () => {
          setDeleting(true)
          const result = await deletePage({ input: { pageId: page.id } })
          setDeleting(false)
          const payload = result.data?.deletePage
          if (payload?.blockedDescendantCount) {
            // Whole cascade refused — nothing was deleted (design.md
            // §6.4.1's "one operation"). Keep the dialog open so the
            // count is visible instead of silently closing on a no-op.
            setBlockedDescendantCount(payload.blockedDescendantCount)
            return
          }
          if (payload?.success) {
            setDeleteOpen(false)
            navigate(page.parent ? `/pages/${page.parent.id}` : `/spaces/${page.spaceKey}`)
          }
        }}
      />
    </Box>
  )
}
