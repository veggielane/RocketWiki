import { useRef, useState } from 'react'
import { Box, Button, Stack, Typography } from '@mui/material'
import { RichTextEditor, type RichTextEditorHandle } from '../editor/RichTextEditor'
import { buildCommentTree, type CommentNode, type FlatComment } from './buildCommentTree'
import { UserAvatar } from '../avatars/UserAvatar'
import { formatTimestamp } from '../format/dateTime'
import { ConfirmDialog } from '../feedback/ConfirmDialog'

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

/**
 * Label only — the shortcut itself is TipTap's 'Mod-Enter', which already
 * maps to Cmd on macOS and Ctrl elsewhere. Same visible-helper-text pattern
 * as Ask's "Enter to ask · Shift+Enter for a new line": a keyboard path
 * that exists but is never announced may as well not exist.
 */
const SUBMIT_KEY_LABEL = /Mac|iPhone|iPad|iPod/.test(typeof navigator === 'undefined' ? '' : navigator.userAgent)
  ? 'Cmd+Enter'
  : 'Ctrl+Enter'
export function Comments({ pageId, comments, canComment, currentUserId, canManageAccess, onAdd, onDelete }: CommentsProps) {
  const tree = buildCommentTree(comments)
  const [replyingToId, setReplyingToId] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const newCommentRef = useRef<RichTextEditorHandle>(null)
  // Every other destructive action in the app is confirmed (page delete, emoji,
  // property key, space archive, GitLab token) — four of them through this same
  // shared dialog. Comment deletion was the one that fired on a single click,
  // beside Reply, at the same size, with no undo. Aggravated by the refetch
  // teardown, which meant there was no moment at which you saw what you had
  // just destroyed.
  const [pendingDelete, setPendingDelete] = useState<CommentNode | null>(null)
  const [deleting, setDeleting] = useState(false)

  const handleAdd = async (parentCommentId: string | null, ref: React.RefObject<RichTextEditorHandle | null>) => {
    // `submitting` guards the keyboard path the same way it disables the
    // button — Ctrl/Cmd+Enter held down must not double-post.
    if (submitting) return
    const body = ref.current?.getMarkdown().trim()
    if (!body) return
    setSubmitting(true)
    try {
      await onAdd(body, parentCommentId)
      // Explicitly, not as a side effect. This used to happen only because
      // posting refetched the page and the whole subtree remounted underneath
      // the composer; the screens keep their data across a refetch now, so
      // without this the text would sit in the box after a successful post.
      //
      // Inside the `try`, after the await: a refused post keeps the text, which
      // is the whole reason the refusal is worth reading.
      ref.current?.clear()
      setReplyingToId(null)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Stack spacing={2}>
      <Typography variant="h5">Comments</Typography>

      {/* Empty-state voice (web/README.md): fact + consequence — but only
          promise the consequence to users who can actually comment. */}
      {tree.length === 0 && (
        <Typography variant="body2" color="text.secondary">
          {canComment ? 'No comments yet — start the discussion below.' : 'No comments yet.'}
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
            onConfirmDelete={setPendingDelete}
          />
        ))}
      </Stack>

      {canComment && (
        <Box>
          <RichTextEditor
            ref={newCommentRef}
            initialMarkdown=""
            showToolbar={false}
            pageId={pageId}
            ariaLabel="New comment"
            ariaDescribedBy="comment-composer-hint"
            onSubmitShortcut={() => void handleAdd(null, newCommentRef)}
          />
          <Stack direction="row" spacing={1.5} sx={{ mt: 1, alignItems: 'center' }}>
            <Button
              variant="contained"
              size="small"
              disabled={submitting}
              onClick={() => void handleAdd(null, newCommentRef)}
            >
              Post comment
            </Button>
            <Typography id="comment-composer-hint" variant="caption" color="text.secondary">
              {SUBMIT_KEY_LABEL} to post · Enter for a new paragraph
            </Typography>
          </Stack>
        </Box>
      )}

      {/* The shared confirm, so "are you sure?" has one shape here too. Names
          the author and the time rather than quoting the body: a comment can be
          long, and the dialog has to stay a question rather than become a
          second rendering of the thing it is about. */}
      <ConfirmDialog
        open={pendingDelete !== null}
        title="Delete this comment?"
        confirmLabel="Delete"
        busy={deleting}
        onCancel={() => setPendingDelete(null)}
        onConfirm={() => {
          const target = pendingDelete
          if (!target) return
          setDeleting(true)
          void onDelete(target.id).finally(() => {
            setDeleting(false)
            setPendingDelete(null)
          })
        }}
      >
        {pendingDelete
          ? `${pendingDelete.authorDisplayName}'s comment from ${formatTimestamp(pendingDelete.createdAtUtc)} will be removed. Replies to it stay, marked as replies to a deleted comment.`
          : ''}
      </ConfirmDialog>
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
  onConfirmDelete: (node: CommentNode) => void
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
  onConfirmDelete,
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
            {formatTimestamp(node.createdAtUtc)}
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
              <Button size="small" color="error" onClick={() => onConfirmDelete(node)}>
                Delete
              </Button>
            )}
          </Stack>
        )}

        {isReplying && (
          <Box sx={{ mt: 1 }}>
            <RichTextEditor
              ref={replyRef}
              initialMarkdown=""
              showToolbar={false}
              ariaLabel={`Reply to ${node.authorDisplayName}`}
              ariaDescribedBy={`comment-reply-hint-${node.id}`}
              onSubmitShortcut={() => onSubmitReply(node.id, replyRef)}
            />
            <Stack direction="row" spacing={1} sx={{ mt: 1, alignItems: 'center' }}>
              <Button size="small" variant="contained" disabled={submitting} onClick={() => onSubmitReply(node.id, replyRef)}>
                Post reply
              </Button>
              <Button size="small" onClick={onCancelReply}>
                Cancel
              </Button>
              <Typography id={`comment-reply-hint-${node.id}`} variant="caption" color="text.secondary">
                {SUBMIT_KEY_LABEL} to post
              </Typography>
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
              onConfirmDelete={onConfirmDelete}
            />
          ))}
        </Stack>
      )}
    </Box>
  )
}
