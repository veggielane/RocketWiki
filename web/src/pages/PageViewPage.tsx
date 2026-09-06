import { useState, type ReactNode } from 'react'
import { useParams, useNavigate, useLocation, Link as RouterLink } from 'react-router-dom'
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
  ListItemIcon,
  Menu,
  MenuItem,
  Skeleton,
  Snackbar,
  Stack,
  Tooltip,
} from '@mui/material'
import MoreVertIcon from '@mui/icons-material/MoreVert'
import EditOutlinedIcon from '@mui/icons-material/EditOutlined'
import NoteAddOutlinedIcon from '@mui/icons-material/NoteAddOutlined'
import LocalOfferOutlinedIcon from '@mui/icons-material/LocalOfferOutlined'
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined'
import NotificationsNoneOutlinedIcon from '@mui/icons-material/NotificationsNoneOutlined'
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive'
import PolicyOutlinedIcon from '@mui/icons-material/PolicyOutlined'
import TuneOutlinedIcon from '@mui/icons-material/TuneOutlined'
import HistoryOutlinedIcon from '@mui/icons-material/HistoryOutlined'
import HubOutlinedIcon from '@mui/icons-material/HubOutlined'
import { graphPath } from '../graph/graphModel'
import {
  usePageAccessByIdQuery,
  useCurrentUserQuery,
  useSpaceReplicaBannerQuery,
  useSpaceLabelDetailsQuery,
  useSpaceTreeForMoveQuery,
  useCreatePageMutation,
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
import { describeLoadFailure, describeWriteFailure, REPLICA_EXPLANATION, replicaBadgeLabel } from '../feedback/unavailableCopy'
import { SNACKBAR_AUTO_HIDE_MS } from '../feedback/snackbar'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { RichTextEditor } from '../editor/RichTextEditor'
import { CreatePageDialog, type CreatePageValues } from './CreatePageDialog'
import { flattenParentOptions } from './parentOptions'
import { readableTree } from './treeEntries'
import { lookupPageIcon } from './pageIcons'
import { ProtectedPageOrNotFound } from '../access/denial/ProtectedPageOrNotFound'
import { DeletePageDialog } from '../trash/DeletePageDialog'
import { ReadOnlyReplicaDialog } from '../feedback/ReadOnlyReplicaDialog'
import { PermissionInspectorPanel } from '../access/permission/PermissionInspectorPanel'
import { AttachmentList } from '../attachments/AttachmentList'
import { AttachmentUploadButton } from '../attachments/AttachmentUploadButton'
import { Comments } from '../comments/Comments'
import { LabelEditor } from '../labels/LabelEditor'
import { computeLabelOps } from '../labels/labelOps'
import { ClassificationBanner } from '../markings/ClassificationBanner'
import { useScrollToHash } from './useScrollToHash'
import { PageIdContext } from './pageContext'
import { PresenceAvatars } from '../presence/PresenceAvatars'
import { usePresenceViewers, useSetPresenceRoom } from '../presence/PresenceRoomContext'
import { pageRoom } from '../presence/presenceRoom'

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
export function PageViewPage({
  pageId: pageIdFromRoute,
  onUnavailable,
}: {
  pageId?: string
  /**
   * What to render instead of the "couldn't load this page" notice AND
   * instead of the protected screen. Only the space-home route passes it: a
   * space whose default page this caller cannot view should behave like a
   * space without one, not become a dead end where the space used to be. The
   * default stays the notice or the protected screen, so the ordinary page
   * routes are unchanged.
   */
  onUnavailable?: ReactNode
} = {}) {
  // Two addresses, one screen: /pages/{id} supplies the id as a route param, and
  // /spaces/{key}/{slug} resolves the slug first and passes the id in. Everything
  // below is identical either way — there is deliberately no second rendering path
  // that could drift from this one.
  const params = useParams<{ pageId: string }>()
  const pageId = pageIdFromRoute ?? params.pageId
  const navigate = useNavigate()
  // The DISCLOSING read (design.md §6.7 / §21.8): page, or a denial the
  // protected screen renders, or null for no such page — one request, one
  // audit row. The plain `page(id)` stays for content-only consumers.
  const [{ data, fetching, error }, refetchPage] = usePageAccessByIdQuery({
    variables: { id: pageId ?? '' },
    pause: !pageId,
  })
  const access = data?.pageAccess
  const [{ data: meData }] = useCurrentUserQuery()
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [blockedCount, setBlockedCount] = useState<number | null>(null)
  const [replicaOrigin, setReplicaOrigin] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [editingLabels, setEditingLabels] = useState(false)
  const [inspectorOpen, setInspectorOpen] = useState(false)
  const [createOpen, setCreateOpen] = useState(false)
  const [createError, setCreateError] = useState<string | null>(null)
  const [actionsAnchor, setActionsAnchor] = useState<HTMLElement | null>(null)
  // A save on the edit screen navigates here and hands its notice over in
  // router state — the editor unmounts, so its own Snackbar cannot outlive the
  // navigation, and a save that confirmed nothing was indistinguishable from
  // one that silently failed. Read once into state so a re-render (or a back
  // navigation onto the same entry) does not resurrect it.
  const location = useLocation()
  const [savedNotice, setSavedNotice] = useState<string | null>(
    () => (location.state as { savedNotice?: string } | null)?.savedNotice ?? null,
  )
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
    setCreateOpen(false)
  }
  // Search results (design.md §9) link to `#anchorId` — the heading isn't
  // in the DOM yet at the moment of navigation (content loads and the
  // editor mounts asynchronously), so this watches for it to appear rather
  // than relying on the browser's one-shot native hash scroll.
  useScrollToHash(access?.page?.content)
  // A denial never reaches this: `access.page` is null then, so the tab keeps
  // the route's own name until the protected screen sets its own.
  useDocumentTitle(access?.page?.title)

  const spaceId = access?.page?.spaceId
  const spaceKey = access?.page?.spaceKey
  const canEdit = access?.page?.canEdit === true
  // The "add child page" dialog needs the space's tree for its parent picker,
  // and only an editor can create one. (The move dialog used to share this
  // query; it has gone to the details screen and fetches its own.)
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
  const [{ fetching: creating }, createPage] = useCreatePageMutation()
  const [, addComment] = useAddCommentMutation()
  const [, deleteComment] = useDeleteCommentMutation()
  const [, deletePage] = useDeletePageMutation()
  const [, watchPage] = useWatchPageMutation()
  const [, unwatchPage] = useUnwatchPageMutation()
  const [, createLabel] = useCreateLabelMutation()
  const [, attachLabel] = useAttachLabelMutation()
  const [, detachLabel] = useDetachLabelMutation()
  // The room only this screen can name. The shell owns presence now — it is
  // the only component that sees every route — but the readable address
  // carries a slug, not an id, so the id has to come from here. Reader and
  // editor of the same page therefore share one room whichever URL they
  // arrived by, and the cursor overlay is the shell-wide one.
  useSetPresenceRoom(pageId ? pageRoom(pageId) : null)
  const viewers = usePresenceViewers()

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

  const watching = watchOverride ?? access?.page?.viewerIsWatching ?? false

  const handleCreateChild = async (values: CreatePageValues) => {
    if (!page) return
    setCreateError(null)
    const result = await createPage({
      input: {
        spaceId: page.spaceId,
        parentPageId: values.parentPageId,
        slug: values.slug,
        title: values.title,
        icon: values.icon,
        content: '',
      },
    })
    if (result.error) {
      setCreateError(describeLoadFailure('PAGE').summary)
      return
    }
    const refused = describeMutationError(result.data?.createPage.error)
    if (refused) {
      setCreateError(refused)
      return
    }
    const created = result.data?.createPage.page
    if (created) {
      setCreateOpen(false)
      navigate(`/pages/${created.id}/edit`)
    }
  }

  const handleToggleWatch = async () => {
    if (!pageId) return
    setActionError(null)
    const next = !watching
    setWatchOverride(next) // optimistic — reverted below if refused

    /**
     * Reverts the optimistic flip AND says why. It used to revert on
     * `result.error` without calling `surfaceError`, so a transport failure
     * flipped the button back with no explanation at all — a control that
     * appears to undo itself, which reads as the app deciding against you
     * rather than as a network that did not answer.
     */
    const revertWithReason = (result: { error?: unknown }, typedError: Parameters<typeof surfaceError>[0]) => {
      if (result.error !== undefined) {
        setActionError(describeWriteFailure('WATCH').summary)
        setWatchOverride(!next)
        return
      }
      if (surfaceError(typedError)) setWatchOverride(!next)
    }

    if (next) {
      const result = await watchPage({ input: { pageId } })
      revertWithReason(result, result.data?.watchPage.error)
      return
    }
    // design.md §8: unwatching is deliberately ungated — treat any
    // response as unwatched unless a typed error says otherwise.
    const result = await unwatchPage({ input: { pageId } })
    revertWithReason(result, result.data?.unwatchPage.error)
  }

    // FIRST LOAD ONLY. urql retains `data` across a refetch and flips `fetching`
  // true (urql.js computeNextState), so a bare `if (fetching)` threw the screen
  // away on every post-write refetch: content, scroll position and keyboard
  // focus all went with it. `&& !data` keeps the rendered screen up while the
  // re-read happens underneath it.
  if (fetching && !data) {
    return (
      <Stack spacing={1}>
        <Skeleton variant="text" width="40%" height={48} />
        <Skeleton variant="rectangular" height={200} />
      </Stack>
    )
  }

  if (error) {
    // The request never got an answer — distinct from the two answers below,
    // because "try the URL again" and "this page is gone" are different advice.
    return onUnavailable ?? <Alert severity="info">{describeLoadFailure('PAGE_ACCESS').summary}</Alert>
  }

  if (!access?.page) {
    // Null: no such page. A denial: the page exists and is withheld — the
    // protected screen says so, with the marking and every failing gate
    // (design.md §6.7 / §21.8), never the title. A caller that substituted
    // its own fallback gets it for both: the space-home route wants a
    // withheld default page to behave like no default page.
    return onUnavailable ?? <ProtectedPageOrNotFound denial={access?.denial} />
  }

  const page = access.page
  const replicaSpace = spaceMeta?.space?.isReplica === true ? spaceMeta.space : null
  // No fallback glyph here, unlike the trees: a row in a list needs its icon
  // slot filled to stay aligned with its neighbours, but a heading has no
  // neighbours, and a generic page icon beside every title would say nothing.
  const TitleIcon = lookupPageIcon(page.icon)?.Icon

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
    // Fences rendered inside the content need the page they sit on, and cannot
    // reach it through props or useParams (a slug route carries no id).
    <PageIdContext value={page.id}>
    <Box>
      {/*
        Nine controls used to sit here in one un-wrapping row, every one of them
        an equally-weighted outlined button: Edit — the thing most readers came
        to do — carried exactly the same visual weight as Watch, and Delete sat
        beside it separated only by a red tint. They also did not fit; the row
        alone runs past the 960px measure and squashed the title.

        So: Edit is the one emphasised action, Watch stays out (it is a toggle
        people use from here), and everything else moves behind an overflow
        menu. Delete is last in that menu, under a divider — the same
        segregation the space settings screen gives Archive, rather than a
        destructive action one pixel from the primary one.
      */}
      <Box sx={{ mb: 2 }}>
        <PageHeader
          title={page.title}
          titleAdornment={
            /* Decorative — the h1 it sits beside is the page's name. Outside
               the heading rather than inside it so the accessible name of the
               heading stays exactly the title. */
            TitleIcon ? <TitleIcon sx={{ fontSize: 32, color: 'text.secondary' }} /> : undefined
          }
          actions={
            <>
              <PresenceAvatars viewers={viewers} />
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
              {page.canEdit && (
                <Button
                  component={RouterLink}
                  to={`/pages/${page.id}/edit`}
                  startIcon={<EditOutlinedIcon />}
                  variant="contained"
                  size="small"
                >
                  Edit
                </Button>
              )}
              <Tooltip title="More actions">
                <IconButton
                  size="small"
                  aria-label="More actions"
                  aria-haspopup="menu"
                  onClick={(e) => setActionsAnchor(e.currentTarget)}
                >
                  <MoreVertIcon fontSize="small" />
                </IconButton>
              </Tooltip>
              <Menu anchorEl={actionsAnchor} open={Boolean(actionsAnchor)} onClose={() => setActionsAnchor(null)}>
                {/* Shown to everyone who can read the page, unlike Details: history is
                    the provenance of content already in front of them, and the page
                    read is the gate that already decided they may see it. */}
                <MenuItem
                  component={RouterLink}
                  to={`/pages/${page.id}/history`}
                  onClick={() => setActionsAnchor(null)}
                >
                  <ListItemIcon>
                    <HistoryOutlinedIcon fontSize="small" />
                  </ListItemIcon>
                  History
                </MenuItem>
                {/* This page, highlighted on the document graph. For every
                    reader, like History: the graph is a read of pages they
                    can already see, and the id in the URL makes the view
                    linkable (design.md §6.7). */}
                <MenuItem
                  component={RouterLink}
                  to={graphPath({ focus: page.id })}
                  onClick={() => setActionsAnchor(null)}
                >
                  <ListItemIcon>
                    <HubOutlinedIcon fontSize="small" />
                  </ListItemIcon>
                  View on graph
                </MenuItem>
                {/* Everything about the page that is not the page. Editors only,
                    matching the screen's own gate — offering a link that answers
                    "this is not for you" would be worse than not offering it. */}
                {page.canEdit && (
                  <MenuItem
                    component={RouterLink}
                    to={`/pages/${page.id}/details`}
                    onClick={() => setActionsAnchor(null)}
                  >
                    <ListItemIcon>
                      <TuneOutlinedIcon fontSize="small" />
                    </ListItemIcon>
                    Details
                  </MenuItem>
                )}
                {/* Unlike the space-level "New page" button, this one CAN be gated
                    honestly: creating a child needs canEdit on the parent, and the
                    page read already carries the server's own canEdit. */}
                {page.canEdit && (
                  <MenuItem
                    onClick={() => {
                      setActionsAnchor(null)
                      setCreateError(null)
                      setCreateOpen(true)
                    }}
                  >
                    <ListItemIcon>
                      <NoteAddOutlinedIcon fontSize="small" />
                    </ListItemIcon>
                    Add child page
                  </MenuItem>
                )}
                {/* Move and Permissions used to sit here. They live on the
                    details screen now — that screen is "everything about this
                    page that is not the page", and both are exactly that.
                    Details is the one entry point, so this menu stays short
                    and the page view stops being where unrelated management
                    accretes. */}
                {page.canEdit && <Divider />}
                {page.canEdit && (
                  <MenuItem
                    onClick={() => {
                      setActionsAnchor(null)
                      setBlockedCount(null)
                      setDeleteOpen(true)
                    }}
                    sx={{ color: 'error.main' }}
                  >
                    <ListItemIcon>
                      <DeleteOutlinedIcon fontSize="small" color="error" />
                    </ListItemIcon>
                    Delete
                  </MenuItem>
                )}
              </Menu>
            </>
          }
        />
      </Box>

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

      {/* No properties panel here any more. Properties are metadata BESIDE the
          page (design.md §20 — that is what keeps them out of the Markdown
          round trip), and they now live in exactly one place: the details
          screen, which any viewer can open read-only. Two renderings of the
          same rows, one of them a panel wedged above the content, was the
          clutter this move exists to remove. */}
      {/* `linkTargets` rode on the same read as the content: every page://
          link in it renders resolved — a router link, an inert (protected)
          marker, or a missing marker (editor/marks/PageLinkView.tsx). */}
      <RichTextEditor
        initialMarkdown={page.content}
        editable={false}
        showToolbar={false}
        linkTargets={page.linkTargets}
      />

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

      <Box sx={{ mt: 4 }}>
        {/* ICDS: one banner, fixed to the bottom of the viewport, so the
            classification of what you are reading stays on screen while you
            scroll rather than only bracketing the content. */}
        <ClassificationBanner
          label={page.marking.label}
          level={page.marking.level}
          scopeLabel="Protective marking for this page"
        />
      </Box>

      <CreatePageDialog
        open={createOpen}
        parentLabel={page.title}
        parentOptions={flattenParentOptions(readableTree(treeData?.pageTree ?? []))}
        defaultParentId={page.id}
        error={createError}
        busy={creating}
        onCancel={() => setCreateOpen(false)}
        onConfirm={(values) => void handleCreateChild(values)}
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

      <Snackbar
        open={savedNotice !== null}
        autoHideDuration={SNACKBAR_AUTO_HIDE_MS}
        onClose={() => setSavedNotice(null)}
      >
        <Alert severity="success" onClose={() => setSavedNotice(null)}>
          {savedNotice}
        </Alert>
      </Snackbar>

      {/* One replica dialog for every write on this page (move, delete,
          watch, comments) — design.md §12: explain the replica, never a raw
          error toast. */}
      <ReadOnlyReplicaDialog
        open={replicaOrigin !== null}
        originInstanceId={replicaOrigin}
        onClose={() => setReplicaOrigin(null)}
      />
    </Box>
    </PageIdContext>
  )
}
