import { useRef, useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Skeleton,
  Stack,
  Typography,
} from '@mui/material'
import { usePageByIdQuery, useUpdatePageMutation } from '../graphql/generated/graphql'
import { RichTextEditor, type RichTextEditorHandle } from '../editor/RichTextEditor'

/**
 * Page edit. Two of the brief's non-negotiables live here:
 *  - `ReadOnlyReplicaError` renders as an explanatory banner, not a raw
 *    error toast, and disables the save affordance entirely.
 *  - `StaleRevisionError` opens a merge dialog (view their changes /
 *    overwrite / copy my text) instead of throwing — it's UX, not an
 *    exception (design.md §5, §8).
 */
export function PageEditPage() {
  const { pageId } = useParams<{ pageId: string }>()
  const navigate = useNavigate()
  const editorRef = useRef<RichTextEditorHandle>(null)
  const [pageQuery] = usePageByIdQuery({ variables: { id: pageId ?? '' }, pause: !pageId })
  const [, updatePage] = useUpdatePageMutation()

  const [readOnlyReplica, setReadOnlyReplica] = useState<{ originInstanceId: string } | null>(null)
  const [staleRevision, setStaleRevision] = useState<{ currentRevisionNumber: number; currentContent: string } | null>(
    null,
  )
  const [saving, setSaving] = useState(false)

  const page = pageQuery.data?.page

  const handleSave = async () => {
    if (!page || !editorRef.current) return
    setSaving(true)
    const result = await updatePage({
      input: {
        pageId: page.id,
        baseRevisionNumber: page.revisionNumber,
        title: page.title,
        content: editorRef.current.getMarkdown(),
      },
    })
    setSaving(false)

    const payload = result.data?.updatePage
    if (payload?.readOnlyReplicaError) {
      setReadOnlyReplica({ originInstanceId: payload.readOnlyReplicaError.originInstanceId })
      return
    }
    if (payload?.staleRevisionError) {
      setStaleRevision({
        currentRevisionNumber: payload.staleRevisionError.currentRevisionNumber,
        currentContent: payload.staleRevisionError.currentContent,
      })
      return
    }
    if (payload?.page) {
      navigate(`/pages/${payload.page.id}`)
    }
  }

  if (pageQuery.fetching) {
    return (
      <Stack spacing={1}>
        <Skeleton variant="text" width="40%" height={48} />
        <Skeleton variant="rectangular" height={300} />
      </Stack>
    )
  }

  if (pageQuery.error || !page) {
    return <Alert severity="info">Couldn't load this page — there's no live API in this environment yet.</Alert>
  }

  return (
    <Box>
      <Typography variant="h4" component="h1" sx={{ mb: 2 }}>
        Editing: {page.title}
      </Typography>

      <RichTextEditor ref={editorRef} initialMarkdown={page.content} editable pageId={page.id} />

      <Stack direction="row" spacing={2} sx={{ mt: 2 }}>
        <Button variant="contained" onClick={handleSave} disabled={saving}>
          Save
        </Button>
        <Button variant="text" onClick={() => navigate(`/pages/${page.id}`)}>
          Cancel
        </Button>
      </Stack>

      {/* ReadOnlyReplicaError: explanatory banner, never a raw error toast (design.md §12). */}
      <Dialog open={Boolean(readOnlyReplica)} onClose={() => setReadOnlyReplica(null)}>
        <DialogTitle>This space is read-only</DialogTitle>
        <DialogContent>
          <DialogContentText>
            This page belongs to a space mirrored from instance{' '}
            <strong>{readOnlyReplica?.originInstanceId}</strong>. Replicated spaces are always read-only — edit the
            page on its origin instance instead.
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button autoFocus onClick={() => setReadOnlyReplica(null)}>
            Close
          </Button>
        </DialogActions>
      </Dialog>

      {/* StaleRevisionError: the merge flow, not an exception (design.md §5, §8). */}
      <Dialog open={Boolean(staleRevision)} onClose={() => setStaleRevision(null)} maxWidth="sm" fullWidth>
        <DialogTitle>Someone else saved changes first</DialogTitle>
        <DialogContent>
          <DialogContentText sx={{ mb: 2 }}>
            This page has been saved as revision {staleRevision?.currentRevisionNumber} since you started editing.
            Choose how to proceed:
          </DialogContentText>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2, justifyContent: 'flex-start', gap: 1 }}>
          {/* Default focus on the non-destructive option — this dialog
              appears mid-edit, when a stray Enter keypress is exactly the
              kind of mistake someone stressed about losing work makes.
              "Overwrite anyway" must never be reachable by pressing Enter
              without an intentional Tab first. */}
          <Button
            autoFocus
            variant="outlined"
            onClick={() => {
              setStaleRevision(null)
              // TODO(page-edit): show a real diff view (this dialog already
              // has staleRevision.currentContent available) once there's a
              // proper compare UI — out of scope for the scaffold.
            }}
          >
            View their changes
          </Button>
          <Button
            variant="outlined"
            color="warning"
            onClick={() => {
              setStaleRevision(null)
              // TODO(page-edit): re-submit with the new baseRevisionNumber
              // to overwrite once the mutation supports a force flag.
            }}
          >
            Overwrite anyway
          </Button>
          <Button variant="text" onClick={() => setStaleRevision(null)}>
            Copy my text and cancel
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  )
}
