import { useEffect, useMemo, useRef, useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Skeleton,
  Snackbar,
  Stack,
  Typography,
} from '@mui/material'
import GroupsOutlinedIcon from '@mui/icons-material/GroupsOutlined'
import {
  useCurrentUserQuery,
  usePageByIdQuery,
  useUpdatePageContentInSessionMutation,
  useUpdatePageContentMutation,
  type MutationErrorFragment,
  type PageIcon,
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
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { useUnsavedChangesGuard } from '../editor/useUnsavedChangesGuard'
import { CLASSIFICATION_BANNER_HEIGHT } from '../markings/ClassificationBanner'
import { describeSave, type SaveOutcome } from '../editor/describeSave'
import { PageIconPicker } from './PageIconPicker'
import { pageHref } from './pageSlug'

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
 *    each successful save/reseed), not the page query's snapshot. Pressing
 *    Save then goes to the page, in a session or not — this used to be
 *    solo-only, but `status` becomes 'collaborating' the moment the session
 *    is joined whether or not anyone else is in it, so the ordinary case of
 *    one person editing alone never navigated and Save appeared to do
 *    nothing. The toast crediting the revision's contributors (which the
 *    server resolved from its session registry) rides along to the page.
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
  const [sessionSaved, setSessionSaved] = useState<SaveOutcome | null>(null)
  const [dirty, setDirty] = useState(false)

  const page = pageQuery.data?.page
  useDocumentTitle(page?.title)

  // The icon is part of what a save writes, so the editor holds a draft of it
  // exactly as it holds a draft of the text. Tracked against the server's
  // value rather than only seeded, because the page read resolves after the
  // first render — the same "adjust state when a prop changes" pattern as
  // CreatePageDialog's parent default. Comparing against the server value (not
  // the page id) is what keeps an in-flight pick: a refetch that returns the
  // icon unchanged leaves the draft alone.
  const [icon, setIcon] = useState<PageIcon | null>(page?.icon ?? null)
  const [trackedIcon, setTrackedIcon] = useState<PageIcon | null>(page?.icon ?? null)
  if (trackedIcon !== (page?.icon ?? null)) {
    setTrackedIcon(page?.icon ?? null)
    setIcon(page?.icon ?? null)
  }

  const session = useCoEditSession(pageId ?? '', {
    enabled: page?.canEdit === true,
    seedMarkdown: page?.content ?? '',
    transport: getDefaultCoEditTransport(),
  })
  const collabActive = session.status === 'collaborating'

  // Same presence join + pointer overlay as the view page; keyed on pageId
  // (route reuse — see usePresence.ts).
  const { viewers, pointers, recordPointer } = usePresence(pageId ?? '', getDefaultPresenceTransport())

  // Solo edits only. In a live session the text is in the shared CRDT the
  // moment it is typed — the other participants have it, and the log-cap flow
  // will persist it — so leaving loses nothing and a confirmation would be
  // asking about a risk that does not exist. A solo draft lives only in this
  // browser tab until Save.
  //
  // Mirrored into a ref because `save` clears it and navigates in the same
  // handler: React batches the state write, so a blocker reading the state
  // would still see "dirty" and challenge a successful save.
  const dirtyRef = useRef(false)
  const markDirty = (next: boolean) => {
    dirtyRef.current = next
    setDirty(next)
  }
  const guardActive = dirty && !collabActive
  const blocker = useUnsavedChangesGuard(() => dirtyRef.current && !collabActive, guardActive)

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
    // `icon` is sent on every save, including when it is null: the server
    // assigns the field unconditionally, so leaving it out of the input is not
    // "don't touch it" — it clears the page's icon.
    const input = { pageId: page.id, expectedRevisionNumber, title: page.title, icon, content: draft }

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

    // The draft is now on the server, so nothing is at risk of being lost and
    // the guard must stand down before any navigation below — otherwise saving
    // would pop the "discard your changes?" dialog on the way out.
    markDirty(false)

    // An automatic save must never move anyone: the log-cap reseed fires on
    // the server's schedule, not the author's, and yanking someone out of the
    // editor mid-sentence because of background housekeeping would be the
    // worst possible moment to navigate.
    if (options?.auto === true) {
      setSessionSaved({ revisionNumber: savedRevisionNumber, contributors, auto: true })
      return
    }

    // Pressing Save goes to the page. Leaving is not destructive here: the save
    // committed a revision, and the session's own Close button remains for
    // stepping out without saving.
    //
    // The toast is set for a MANUAL save too, not only the automatic one. It
    // used to be inside the `auto` branch above, which meant a manual save
    // navigated in silence — indistinguishable from a navigation that just
    // happened — and made the 'Saved' half of the toast's own label
    // unreachable, along with the contributor credit the server had gone to
    // trouble to compute. Snackbars survive the navigation because they are
    // rendered by this component's replacement on the page view? They are not:
    // this component unmounts. So the notice is handed to the destination
    // through router state, and PageViewPage shows it on arrival.
    navigate(pageHref(page.spaceKey, page.slug, page.id), {
      state: {
        savedNotice: describeSave({ revisionNumber: savedRevisionNumber, contributors, auto: false }),
      },
    })
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
  /** What the Save button and Ctrl+S both do — one revision choice, not two. */
  const saveNow = () =>
    save(
      collabActive ? (session.baseRevisionNumber ?? page?.currentRevisionNumber ?? 0) : (page?.currentRevisionNumber ?? 0),
    )

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
        <Button
          variant="outlined"
          onClick={() => navigate(pageHref(page.spaceKey, page.slug, page.id))}
          sx={{ alignSelf: 'flex-start' }}
        >
          Back to page
        </Button>
      </Stack>
    )
  }

  return (
    <Box>
      <Box sx={{ mb: 2 }}>
        <PageHeader
          title="Editing"
          subject={{ label: page.title, to: pageHref(page.spaceKey, page.slug, page.id) }}
          actions={
            <>
              {/* Beside the title, because that is what it labels — and the same
                  control the create dialog uses, so the two cannot drift. */}
              <PageIconPicker
                value={icon}
                onChange={setIcon}
                size="small"
                disabled={saving || session.status === 'evicted'}
              />
              {collabActive && (
                <Chip
                  size="small"
                  color="success"
                  variant="outlined"
                  icon={<GroupsOutlinedIcon />}
                  label="Live co-editing"
                />
              )}
              <PresenceAvatars viewers={viewers} />
            </>
          }
        />
      </Box>

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
            onDocChanged={() => markDirty(true)}
            onSaveShortcut={() => void saveNow()}
          />
          <PresencePointers pointers={pointers} />
        </Box>
      )}

      {saveError && (
        <Alert severity="warning" sx={{ mt: 2 }} onClose={() => setSaveError(null)}>
          {saveError}
        </Alert>
      )}

      {/*
        Sticky, not appended below the editor. The editor grows with the
        document, so on a long page Save sat below the entire text — the one
        control the screen exists for, reachable only by scrolling past
        everything. `bottom` clears the fixed classification banner, which is
        also viewport-fixed and would otherwise sit on top of it.
      */}
      <Stack
        direction="row"
        spacing={2}
        useFlexGap
        sx={{
          position: 'sticky',
          bottom: `${CLASSIFICATION_BANNER_HEIGHT}px`,
          zIndex: 1,
          mt: 2,
          py: 1.5,
          flexWrap: 'wrap',
          alignItems: 'center',
          bgcolor: 'background.default',
          borderTop: '1px solid',
          borderColor: 'divider',
        }}
      >
        <Button
          variant="contained"
          onClick={() => void saveNow()}
          disabled={saving || session.status === 'connecting' || session.status === 'evicted'}
        >
          Save
        </Button>
        <Button variant="text" onClick={() => navigate(pageHref(page.spaceKey, page.slug, page.id))}>
          {collabActive ? 'Close' : 'Cancel'}
        </Button>
        {/* Says the shortcut exists — an unannounced accelerator is one only
            the person who wrote it knows about. */}
        <Typography variant="caption" color="text.secondary">
          {dirty ? 'Unsaved changes · ' : ''}Ctrl+S saves
        </Typography>
      </Stack>

      <Snackbar open={sessionSaved !== null} autoHideDuration={SNACKBAR_AUTO_HIDE_MS} onClose={() => setSessionSaved(null)}>
        <Alert severity="success" onClose={() => setSessionSaved(null)}>
          {sessionSaved ? describeSave(sessionSaved) : ''}
        </Alert>
      </Snackbar>

      {/*
        Leaving with unsaved work. A dialog rather than `window.confirm`,
        matching how every other designed choice in this app is presented
        (web/README.md) — and it can name the third option, which the browser's
        own prompt cannot: staying is not the only alternative to discarding.
      */}
      <Dialog open={blocker.state === 'blocked'} onClose={() => blocker.reset?.()}>
        <DialogTitle>Leave without saving?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            This page has changes that have not been saved. Leaving now discards them.
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          {/* Default focus on the non-destructive option, as everywhere else a
              dialog stands between someone and losing work. */}
          <Button autoFocus onClick={() => blocker.reset?.()}>
            Keep editing
          </Button>
          <Button
            variant="outlined"
            onClick={() => {
              blocker.reset?.()
              void saveNow()
            }}
          >
            Save and leave
          </Button>
          <Button color="error" variant="contained" onClick={() => blocker.proceed?.()}>
            Discard changes
          </Button>
        </DialogActions>
      </Dialog>

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
            navigate(pageHref(page.spaceKey, page.slug, page.id))
          })()
        }}
        onKeepEditing={() => setConflict(null)}
      />
    </Box>
  )
}
