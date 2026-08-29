import { useEffect, useMemo, useRef, useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { Alert, Box, Button, Chip, Skeleton, Snackbar, Stack, Typography } from '@mui/material'
import GroupsOutlinedIcon from '@mui/icons-material/GroupsOutlined'
import {
  useCurrentUserQuery,
  usePageByIdQuery,
  useUpdatePageContentInSessionMutation,
  useUpdatePageContentMutation,
  type MutationErrorFragment,
} from '../graphql/generated/graphql'
import { asReadOnlyReplica, asStaleRevision, describeMutationError, type StaleRevision } from '../graphql/mutationError'
import { SNACKBAR_AUTO_HIDE_MS } from '../feedback/snackbar'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { RichTextEditor, type RichTextEditorHandle, type CollabBinding } from '../editor/RichTextEditor'
import { useCoEditSession } from '../editor/coedit/useCoEditSession'
import { usePresence } from '../presence/usePresence'
import { PresenceAvatars } from '../presence/PresenceAvatars'
import { PresencePointers } from '../presence/PresencePointers'
import { colourForUser } from '../presence/colourForUser'
import { getDefaultCoEditTransport, getDefaultPresenceTransport } from '../realtime/transports'
import { ReadOnlyReplicaDialog } from '../feedback/ReadOnlyReplicaDialog'
import { StaleRevisionDialog } from '../diff/StaleRevisionDialog'

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
 *
 * Co-editing (design.md §8) is a PROGRESSIVE ENHANCEMENT on top of that:
 * on mount the page asks the hub for an edit session (useCoEditSession).
 * A successful join mounts the editor in collaborative mode (Yjs doc +
 * live carets); a refused/failed join — no canEdit via the hub, a replica,
 * the hub unreachable, `VITE_FAKE_REALTIME` — falls back to the EXACT solo
 * path above, indistinguishable from before co-editing existed. In
 * collaborative mode:
 *  - Save uses the SESSION's base revision (from the join, advanced by
 *    each successful save/reseed), not the page query's snapshot — and a
 *    successful save stays in the editor (a checkpoint in a live session,
 *    not an exit) with a toast crediting the revision's contributors,
 *    which the server resolved from its session registry.
 *  - A `log_cap` reseed demand auto-saves and hands back a snapshot,
 *    surfaced only as the same save toast (marked autosaved): prompting
 *    would stall the session while the log kept growing, and the write is
 *    honest — the server credits the saver AND every contributor.
 *  - Eviction (canEdit revoked mid-session) drops the editor to read-only
 *    with clear copy and no rejoin loop.
 *
 * Presence (viewers + live pointers) mounts here exactly as on the view
 * page — same JoinPage channel, which is canView-gated server-side, and
 * editing implies viewing.
 */
export function PageEditPage() {
  const { pageId } = useParams<{ pageId: string }>()
  const navigate = useNavigate()
  const editorRef = useRef<RichTextEditorHandle>(null)
  const [pageQuery] = usePageByIdQuery({ variables: { id: pageId ?? '' }, pause: !pageId })
  const [{ data: meData }] = useCurrentUserQuery()
  const [, updatePageContent] = useUpdatePageContentMutation()
  const [, updatePageContentInSession] = useUpdatePageContentInSessionMutation()

  const [replicaOrigin, setReplicaOrigin] = useState<string | null>(null)
  const [conflict, setConflict] = useState<{ stale: StaleRevision; draft: string } | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [sessionSaved, setSessionSaved] = useState<{
    revisionNumber: number
    contributors: string[]
    auto: boolean
  } | null>(null)

  const page = pageQuery.data?.page

  const session = useCoEditSession(pageId ?? '', {
    enabled: page?.canEdit === true,
    seedMarkdown: page?.content ?? '',
    transport: getDefaultCoEditTransport(),
  })
  const collabActive = session.status === 'collaborating'

  // Same presence join + pointer overlay as the view page; keyed on pageId
  // (route reuse — see usePresence.ts).
  const { viewers, pointers, recordPointer } = usePresence(pageId ?? '', getDefaultPresenceTransport())

  const collabBinding = useMemo<CollabBinding | undefined>(() => {
    if ((session.status !== 'collaborating' && session.status !== 'evicted') || !session.provider) return undefined
    // Loaded before the join resolved (same Promise.all as the provider) —
    // this is belt-and-braces narrowing, not an extra wait state.
    if (!session.extensionsModule) return undefined
    const userId = meData?.me.localUserId ?? undefined
    return {
      doc: session.provider.doc,
      provider: session.provider,
      user: {
        name: meData?.me.name ?? meData?.me.email ?? 'Someone',
        color: colourForUser(userId ?? meData?.me.id ?? ''),
        userId,
      },
      extensionsModule: session.extensionsModule,
    }
  }, [session.status, session.provider, session.extensionsModule, meData])

  const save = async (expectedRevisionNumber: number, options?: { auto?: boolean }) => {
    if (!page || !editorRef.current) return
    setSaving(true)
    setSaveError(null)
    const draft = editorRef.current.getMarkdown()
    const input = { pageId: page.id, expectedRevisionNumber, title: page.title, content: draft }

    // Session saves use the contributor-selecting document; the solo path
    // keeps the original one — see page.graphql on why they're separate.
    let error: MutationErrorFragment | null | undefined
    let savedRevisionNumber: number | null = null
    let contributors: string[] = []
    if (collabActive) {
      const result = await updatePageContentInSession({ input })
      const payload = result.data?.updatePageContent
      error = payload?.error
      if (payload?.page) {
        savedRevisionNumber = payload.page.currentRevisionNumber
        contributors =
          payload.page.revisions
            .find((r) => r.revisionNumber === savedRevisionNumber)
            ?.contributors.map((c) => c.displayName) ?? []
      }
    } else {
      const result = await updatePageContent({ input })
      const payload = result.data?.updatePageContent
      error = payload?.error
      if (payload?.page) savedRevisionNumber = payload.page.currentRevisionNumber
    }
    setSaving(false)

    const replica = asReadOnlyReplica(error)
    if (replica) {
      setReplicaOrigin(replica.originInstanceId ?? 'its origin instance')
      return
    }
    const stale = asStaleRevision(error)
    if (stale) {
      // Capture the draft as submitted: it's what the conflict is *about*,
      // and what the dialog diffs against their latest revision.
      setConflict({ stale, draft })
      return
    }
    const errorText = describeMutationError(error)
    if (errorText) {
      setSaveError(errorText)
      return
    }
    if (savedRevisionNumber === null) return

    // Session bookkeeping happens either way, and before any navigation:
    // advance the tracked base (the server advanced its copy in the same
    // transaction) and settle a pending log-cap reseed with a fresh
    // full-state snapshot, which the OTHER participants depend on.
    if (collabActive) {
      session.noteSaved(savedRevisionNumber)
      if (session.reseedDemand !== null) {
        await session.completeReseed(savedRevisionNumber)
      }
    }

    // An automatic save must never move anyone: the log-cap reseed fires on
    // the server's schedule, not the author's, and yanking someone out of the
    // editor mid-sentence because of background housekeeping would be the
    // worst possible moment to navigate.
    if (options?.auto === true) {
      setSessionSaved({ revisionNumber: savedRevisionNumber, contributors, auto: true })
      return
    }

    // Pressing Save goes to the page, in a session or not. This used to be
    // solo-only, on the reasoning that "a session save is a checkpoint, not an
    // exit" — but `status` becomes 'collaborating' the moment the edit session
    // is joined, whether or not anyone else is in it, so the ordinary case of
    // one person editing alone never navigated and Save appeared to do nothing.
    // Leaving is not destructive here: the save committed a revision, and the
    // session's own Close button remains for stepping out without saving.
    navigate(`/pages/${page.id}`)
  }

  // The log_cap flow (design.md §8, hub doc): the server named THIS client
  // to save-and-reseed. Auto-save rather than prompt: a prompt would stall
  // the session while the log kept growing past its cap, and the write is
  // honest attribution-wise — AuthorUserId is whoever pressed save (here:
  // this user, via automation they were told about in the toast), and the
  // server credits every session contributor regardless. A StaleRevision
  // during the auto-save opens the normal conflict dialog instead of
  // overwriting anyone silently; the demand then stays pending and the
  // next successful session save completes it (see `save` above).
  const reseedSaveInFlight = useRef(false)
  useEffect(() => {
    if (session.reseedDemand === null || !collabActive || reseedSaveInFlight.current) return
    reseedSaveInFlight.current = true
    void save(session.reseedDemand, { auto: true }).finally(() => {
      reseedSaveInFlight.current = false
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps -- deliberately demand-keyed: including `save` would re-fire per render, and after a StaleRevision it would retry the conflicted auto-save forever instead of leaving the demand pending for the user's resolution
  }, [session.reseedDemand, collabActive])

  if (pageQuery.fetching) {
    return (
      <Stack spacing={1}>
        <Skeleton variant="text" width="40%" height={48} />
        <Skeleton variant="rectangular" height={300} />
      </Stack>
    )
  }

  if (pageQuery.error || !page) {
    return <Alert severity="info">{describeLoadFailure('PAGE').summary}</Alert>
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
      <Stack direction="row" sx={{ mb: 2, alignItems: 'center', justifyContent: 'space-between' }}>
        <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
          <Typography variant="h4" component="h1">
            Editing: {page.title}
          </Typography>
          {collabActive && (
            <Chip
              size="small"
              color="success"
              variant="outlined"
              icon={<GroupsOutlinedIcon />}
              label="Live co-editing"
            />
          )}
        </Stack>
        <PresenceAvatars viewers={viewers} />
      </Stack>

      {session.status === 'evicted' && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          Your edit access to this page was revoked, so the live editing session ended. The editor below is
          read-only and no longer synced — copy anything you still need before leaving.
        </Alert>
      )}

      {session.oversizedUpdate && (
        <Alert severity="warning" sx={{ mb: 2 }} onClose={session.clearOversizedUpdate}>
          Your last change was too large to sync live to the other editors. It is still in your editor and
          will reach everyone when the page is saved.
        </Alert>
      )}

      {session.status === 'connecting' ? (
        // Deciding collab-vs-solo must precede editor creation (the
        // Collaboration extension replaces the editor's history), so this
        // brief gap gets a skeleton, bounded by the provider's join
        // timeout — a dead hub degrades to solo, never to a dead editor.
        <Skeleton variant="rectangular" height={300} />
      ) : (
        <Box
          sx={{ position: 'relative' }}
          onMouseMove={(e) => {
            const rect = e.currentTarget.getBoundingClientRect()
            if (rect.width === 0 || rect.height === 0) return
            recordPointer((e.clientX - rect.left) / rect.width, (e.clientY - rect.top) / rect.height)
          }}
        >
          <RichTextEditor
            key={`${page.id}:${collabBinding ? 'collab' : 'solo'}`}
            ref={editorRef}
            initialMarkdown={page.content}
            editable={session.status !== 'evicted'}
            pageId={page.id}
            collab={collabBinding}
          />
          <PresencePointers pointers={pointers} />
        </Box>
      )}

      {saveError && (
        <Alert severity="warning" sx={{ mt: 2 }} onClose={() => setSaveError(null)}>
          {saveError}
        </Alert>
      )}

      <Stack direction="row" spacing={2} sx={{ mt: 2 }}>
        <Button
          variant="contained"
          onClick={() =>
            void save(collabActive ? (session.baseRevisionNumber ?? page.currentRevisionNumber) : page.currentRevisionNumber)
          }
          disabled={saving || session.status === 'connecting' || session.status === 'evicted'}
        >
          Save
        </Button>
        <Button variant="text" onClick={() => navigate(`/pages/${page.id}`)}>
          {collabActive ? 'Close' : 'Cancel'}
        </Button>
      </Stack>

      <Snackbar open={sessionSaved !== null} autoHideDuration={SNACKBAR_AUTO_HIDE_MS} onClose={() => setSessionSaved(null)}>
        <Alert severity="success" onClose={() => setSessionSaved(null)}>
          {sessionSaved?.auto ? 'Autosaved' : 'Saved'} revision {sessionSaved?.revisionNumber}
          {sessionSaved && sessionSaved.contributors.length > 0
            ? ` — contributors: ${sessionSaved.contributors.join(', ')}`
            : ''}
        </Alert>
      </Snackbar>

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
