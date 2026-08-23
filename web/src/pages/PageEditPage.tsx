import { useRef, useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { Alert, Box, Button, Skeleton, Stack, Typography } from '@mui/material'
import { usePageByIdQuery, useUpdatePageContentMutation } from '../graphql/generated/graphql'
import { asReadOnlyReplica, asStaleRevision, describeMutationError, type StaleRevision } from '../graphql/mutationError'
import { RichTextEditor, type RichTextEditorHandle } from '../editor/RichTextEditor'
import { ReadOnlyReplicaDialog } from './ReadOnlyReplicaDialog'
import { StaleRevisionDialog } from './StaleRevisionDialog'

/**
 * Page edit. Two of the brief's non-negotiables live here, both driven by
 * the API's flattened typed-error payload (`error.kind`, see
 * graphql/mutationError.ts):
 *  - `ReadOnlyReplica` renders as an explanatory dialog carrying the
 *    origin instance id, not a raw error toast (design.md §12).
 *  - `StaleRevision` opens the merge flow (view their changes /
 *    overwrite / copy my text) instead of throwing — it's UX, not an
 *    exception (design.md §5, §8). The dialog shows a diff of the error's
 *    `latestContent` against the draft as submitted at conflict time
 *    (captured here, since the editor stays live behind the dialog).
 *    "Overwrite anyway" re-submits against the revision number the error
 *    reported; "Copy my text and cancel" puts the draft on the clipboard
 *    and leaves their revision standing.
 */
export function PageEditPage() {
  const { pageId } = useParams<{ pageId: string }>()
  const navigate = useNavigate()
  const editorRef = useRef<RichTextEditorHandle>(null)
  const [pageQuery] = usePageByIdQuery({ variables: { id: pageId ?? '' }, pause: !pageId })
  const [, updatePageContent] = useUpdatePageContentMutation()

  const [replicaOrigin, setReplicaOrigin] = useState<string | null>(null)
  const [conflict, setConflict] = useState<{ stale: StaleRevision; draft: string } | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const page = pageQuery.data?.page

  const save = async (expectedRevisionNumber: number) => {
    if (!page || !editorRef.current) return
    setSaving(true)
    setSaveError(null)
    const draft = editorRef.current.getMarkdown()
    const result = await updatePageContent({
      input: {
        pageId: page.id,
        expectedRevisionNumber,
        title: page.title,
        content: draft,
      },
    })
    setSaving(false)

    const payload = result.data?.updatePageContent
    const replica = asReadOnlyReplica(payload?.error)
    if (replica) {
      setReplicaOrigin(replica.originInstanceId ?? 'its origin instance')
      return
    }
    const stale = asStaleRevision(payload?.error)
    if (stale) {
      // Capture the draft as submitted: it's what the conflict is *about*,
      // and what the dialog diffs against their latest revision.
      setConflict({ stale, draft })
      return
    }
    const errorText = describeMutationError(payload?.error)
    if (errorText) {
      setSaveError(errorText)
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
    return <Alert severity="info">Couldn't load this page.</Alert>
  }

  if (!page.canEdit) {
    // The edit route is reachable by URL even though the view page hides
    // its Edit button — mirror the server's verdict rather than mounting
    // an editor whose save can only be refused. This is not a leak: the
    // caller can already view the page, and canEdit is their own
    // permission (design.md §6.7 applies to reads, not to telling a user
    // what they themselves may do).
    return (
      <Stack spacing={2}>
        <Alert severity="info">You don't have permission to edit this page.</Alert>
        <Button variant="outlined" onClick={() => navigate(`/pages/${page.id}`)} sx={{ alignSelf: 'flex-start' }}>
          Back to page
        </Button>
      </Stack>
    )
  }

  return (
    <Box>
      <Typography variant="h4" component="h1" sx={{ mb: 2 }}>
        Editing: {page.title}
      </Typography>

      <RichTextEditor ref={editorRef} initialMarkdown={page.content} editable pageId={page.id} />

      {saveError && (
        <Alert severity="warning" sx={{ mt: 2 }} onClose={() => setSaveError(null)}>
          {saveError}
        </Alert>
      )}

      <Stack direction="row" spacing={2} sx={{ mt: 2 }}>
        <Button variant="contained" onClick={() => void save(page.currentRevisionNumber)} disabled={saving}>
          Save
        </Button>
        <Button variant="text" onClick={() => navigate(`/pages/${page.id}`)}>
          Cancel
        </Button>
      </Stack>

      <ReadOnlyReplicaDialog
        open={replicaOrigin !== null}
        originInstanceId={replicaOrigin}
        onClose={() => setReplicaOrigin(null)}
      />

      <StaleRevisionDialog
        open={conflict !== null}
        currentRevisionNumber={conflict?.stale.actualRevisionNumber ?? null}
        yourTitle={page.title}
        yourDraft={conflict?.draft ?? ''}
        theirTitle={conflict?.stale.latestTitle ?? null}
        theirContent={conflict?.stale.latestContent ?? null}
        onOverwriteAnyway={() => {
          const theirRevision = conflict?.stale.actualRevisionNumber
          setConflict(null)
          // Re-submit against the revision the error reported as current —
          // an explicit, informed overwrite of exactly the revision the
          // user was just told about (a *newer* save landing in between
          // will surface as another StaleRevision, as it should).
          if (theirRevision != null) void save(theirRevision)
        }}
        onCopyAndCancel={() => {
          // "Take theirs, but don't lose mine": draft to the clipboard,
          // then leave the editor with their revision standing. If the
          // clipboard refuses (permissions, insecure context), stay put —
          // navigating away would silently destroy the draft.
          void (async () => {
            const draft = conflict?.draft ?? ''
            try {
              await navigator.clipboard.writeText(draft)
            } catch {
              setConflict(null)
              setSaveError("Couldn't copy your draft to the clipboard — it is still in the editor below.")
              return
            }
            setConflict(null)
            navigate(`/pages/${page.id}`)
          })()
        }}
        onKeepEditing={() => setConflict(null)}
      />
    </Box>
  )
}
