import { useRef, useState } from 'react'
import { Box, Button, Stack, Typography } from '@mui/material'
import { RichTextEditor, type RichTextEditorHandle } from '../editor/RichTextEditor'
import { buildCommentTree, type CommentNode, type FlatComment } from './buildCommentTree'
import { UserAvatar } from '../avatars/UserAvatar'

export interface CommentsProps {
  pageId: string
  comments: FlatComment[]
  canComment: boolean
  /** Deleting a comment is allowed for its own author, or someone who can manage this page's access (space-admin/instance-admin), not any commenter. */
  currentUserId: string | undefined
  canManageAccess: boolean
  onAdd: (body: string, parentCommentId: string | null) => Promise<void>
  onDelete: (commentId: string) => Promise<void>
}

/**
 * design.md §5: page-level threaded comments with Markdown bodies. Reuses
 * `RichTextEditor` for composing, not a plain textarea — "one renderer"
 * (design.md §4) covers comment bodies too, not just page content.
 */
export function Comments({ pageId, comments, canComment, currentUserId, canManageAccess, onAdd, onDelete }: CommentsProps) {
  const tree = buildCommentTree(comments)
  const [replyingToId, setReplyingToId] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const newCommentRef = useRef<RichTextEditorHandle>(null)

  const handleAdd = async (parentCommentId: string | null, ref: React.RefObject<RichTextEditorHandle | null>) => {
    const body = ref.current?.getMarkdown().trim()
    if (!body) return
    setSubmitting(true)
    try {
      await onAdd(body, parentCommentId)
      setReplyingToId(null)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Stack spacing={2}>
      <Typography variant="h5">Comments</Typography>

      {tree.length === 0 && (
        <Typography variant="body2" color="text.secondary">
          No comments yet.
        </Typography>
      )}

      <Stack spacing={2}>
        {tree.map((node) => (
          <CommentItem
            key={node.id}
            node={node}
            canComment={canComment}
            currentUserId={currentUserId}
            canManageAccess={canManageAccess}
            replyingToId={replyingToId}
            submitting={submitting}
            onStartReply={setReplyingToId}
            onCancelReply={() => setReplyingToId(null)}
            onSubmitReply={handleAdd}
            onDelete={onDelete}
          />
        ))}
      </Stack>

      {canComment && (
        <Box>
          <RichTextEditor ref={newCommentRef} initialMarkdown="" showToolbar={false} pageId={pageId} />
          <Button
            variant="contained"
            size="small"
            sx={{ mt: 1 }}
            disabled={submitting}
            onClick={() => void handleAdd(null, newCommentRef)}
          >
            Post comment
          </Button>
        </Box>
      )}
    </Stack>
  )
}

interface CommentItemProps {
  node: CommentNode
  canComment: boolean
  currentUserId: string | undefined
  canManageAccess: boolean
  replyingToId: string | null
  submitting: boolean
  onStartReply: (id: string) => void
  onCancelReply: () => void
  onSubmitReply: (parentCommentId: string, ref: React.RefObject<RichTextEditorHandle | null>) => void
  onDelete: (commentId: string) => Promise<void>
}

function CommentItem({
  node,
  canComment,
  currentUserId,
  canManageAccess,
  replyingToId,
  submitting,
  onStartReply,
  onCancelReply,
  onSubmitReply,
  onDelete,
}: CommentItemProps) {
  const replyRef = useRef<RichTextEditorHandle>(null)
  const isReplying = replyingToId === node.id
  const canDeleteThis = node.authorUserId === currentUserId || canManageAccess

  return (
    <Box
      sx={{
        pl: node.parentCommentId ? 2 : 0,
        borderLeft: node.parentCommentId ? '2px solid' : 'none',
        borderColor: 'divider',
      }}
    >
      <Stack spacing={0.5}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          {/* Face + name (design.md §19): image only when `hasAvatar` says
              so; the initials chip is the unchanged fallback. One fetch per
              author per session via the avatar cache, not per comment row. */}
          <UserAvatar
            userId={node.authorUserId}
            hasAvatar={node.authorHasAvatar ?? false}
            displayName={node.authorDisplayName}
            size={24}
          />
          <Typography variant="subtitle2">{node.authorDisplayName}</Typography>
          <Typography variant="caption" color="text.secondary">
            {new Date(node.createdAtUtc).toLocaleString()}
          </Typography>
          {node.editedAtUtc && (
            <Typography variant="caption" color="text.secondary">
              (edited)
            </Typography>
          )}
        </Stack>

        {node.isDeleted ? (
          <Typography variant="body2" color="text.secondary" sx={{ fontStyle: 'italic' }}>
            This comment was deleted.
          </Typography>
        ) : (
          <RichTextEditor initialMarkdown={node.body} editable={false} showToolbar={false} />
        )}

        {!node.isDeleted && (
          <Stack direction="row" spacing={1}>
            {canComment && (
              <Button size="small" onClick={() => onStartReply(node.id)}>
                Reply
              </Button>
            )}
            {canComment && canDeleteThis && (
              <Button size="small" color="error" onClick={() => void onDelete(node.id)}>
                Delete
              </Button>
            )}
          </Stack>
        )}

        {isReplying && (
          <Box sx={{ mt: 1 }}>
            <RichTextEditor ref={replyRef} initialMarkdown="" showToolbar={false} />
            <Stack direction="row" spacing={1} sx={{ mt: 1 }}>
              <Button size="small" variant="contained" disabled={submitting} onClick={() => onSubmitReply(node.id, replyRef)}>
                Post reply
              </Button>
              <Button size="small" onClick={onCancelReply}>
                Cancel
              </Button>
            </Stack>
          </Box>
        )}
      </Stack>

      {node.children.length > 0 && (
        <Stack spacing={2} sx={{ mt: 2 }}>
          {node.children.map((child) => (
            <CommentItem
              key={child.id}
              node={child}
              canComment={canComment}
              currentUserId={currentUserId}
              canManageAccess={canManageAccess}
              replyingToId={replyingToId}
              submitting={submitting}
              onStartReply={onStartReply}
              onCancelReply={onCancelReply}
              onSubmitReply={onSubmitReply}
              onDelete={onDelete}
            />
          ))}
        </Stack>
      )}
    </Box>
  )
}
