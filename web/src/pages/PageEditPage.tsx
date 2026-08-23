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
 *    exception (design.md §5, §8). "Overwrite anyway" re-submits against
 *    the revision number the error reported.
 */
export function PageEditPage() {
  const { pageId } = useParams<{ pageId: string }>()
  const navigate = useNavigate()
  const editorRef = useRef<RichTextEditorHandle>(null)
  const [pageQuery] = usePageByIdQuery({ variables: { id: pageId ?? '' }, pause: !pageId })
  const [, updatePageContent] = useUpdatePageContentMutation()

  const [replicaOrigin, setReplicaOrigin] = useState<string | null>(null)
  const [staleRevision, setStaleRevision] = useState<StaleRevision | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const page = pageQuery.data?.page

  const save = async (expectedRevisionNumber: number) => {
    if (!page || !editorRef.current) return
    setSaving(true)
    setSaveError(null)
    const result = await updatePageContent({
      input: {
        pageId: page.id,
        expectedRevisionNumber,
        title: page.title,
        content: editorRef.current.getMarkdown(),
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
      setStaleRevision(stale)
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
        open={staleRevision !== null}
        currentRevisionNumber={staleRevision?.actualRevisionNumber ?? null}
        onViewChanges={() => {
          setStaleRevision(null)
          // TODO(page-edit): show a real diff view (staleRevision.latestContent
          // is already here) once there's a proper compare UI.
        }}
        onOverwriteAnyway={() => {
          const theirRevision = staleRevision?.actualRevisionNumber
          setStaleRevision(null)
          // Re-submit against the revision the error reported as current —
          // an explicit, informed overwrite of exactly the revision the
          // user was just told about (a *newer* save landing in between
          // will surface as another StaleRevision, as it should).
          if (theirRevision != null) void save(theirRevision)
        }}
        onCopyAndCancel={() => setStaleRevision(null)}
        onClose={() => setStaleRevision(null)}
      />
    </Box>
  )
}
