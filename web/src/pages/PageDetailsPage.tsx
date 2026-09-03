import { useMemo, useState } from 'react'
import { Link as RouterLink, Navigate, useParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Divider,
  IconButton,
  MenuItem,
  Paper,
  Skeleton,
  Snackbar,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import { visuallyHidden } from '@mui/utils'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined'
import DriveFileMoveOutlinedIcon from '@mui/icons-material/DriveFileMoveOutlined'
import ShieldOutlinedIcon from '@mui/icons-material/ShieldOutlined'
import {
  usePagePropertiesForPageQuery,
  usePagePropertyKeysQuery,
  useRemovePagePropertyMutation,
  useSetPagePropertyMutation,
  useSpaceReplicaBannerQuery,
  useSpaceTreeForMoveQuery,
  useMovePageMutation,
  type PagePropertiesForPageQuery,
  type PagePropertyKeysQuery,
} from '../graphql/generated/graphql'
import { asReadOnlyReplica, describeMutationError } from '../graphql/mutationError'
import {
  REPLICA_EXPLANATION,
  describeLoadFailure,
  describeWriteFailure,
  replicaBadgeLabel,
} from '../feedback/unavailableCopy'
import { ReadOnlyReplicaDialog } from '../feedback/ReadOnlyReplicaDialog'
import { SNACKBAR_AUTO_HIDE_MS } from '../feedback/snackbar'
import { MovePageDialog } from '../access/move/MovePageDialog'
import { ancestorRestrictionsOf, flattenMoveTargets, nextSortOrderByTarget } from '../access/move/flattenMoveTargets'
import { readableTree } from './treeEntries'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { PageMarkingSection } from '../markings/PageMarkingSection'
import { hasPendingEdits, resolvePropertyEdit } from '../properties/propertyEdit'
import { MAX_PROPERTY_VALUE_LENGTH } from '../properties/propertyLimits'

type PropertyValue = NonNullable<PagePropertiesForPageQuery['page']>['properties'][number]
type PropertyKeyRef = PagePropertyKeysQuery['pagePropertyKeys'][number]

interface Feedback {
  /** Routed to the right surface by the caller: `notice` is a snackbar, `error` an inline Alert. */
  notice: string | null
  error: string | null
}

interface PropertiesEditorProps {
  pageId: string
  properties: PropertyValue[]
  registry: PropertyKeyRef[]
  registryUnavailable: boolean
  canEdit: boolean
  onFeedback: (feedback: Feedback) => void
  onReplicaRefusal: (originInstanceId: string) => void
  onChanged: () => void
}

function PropertiesEditor({
  pageId,
  properties,
  registry,
  registryUnavailable,
  canEdit,
  onFeedback,
  onReplicaRefusal,
  onChanged,
}: PropertiesEditorProps) {
  const [, setPageProperty] = useSetPagePropertyMutation()
  const [, removePageProperty] = useRemovePagePropertyMutation()
  // Drafts are seeded from the server rows; the whole editor is remounted
  // with a key derived from those rows after a refetch, so there is no
  // effect syncing state to props.
  const [drafts, setDrafts] = useState<Record<string, string>>(() =>
    Object.fromEntries(properties.map((property) => [property.keyId, property.value])),
  )
  const [newKeyId, setNewKeyId] = useState('')
  const [newValue, setNewValue] = useState('')
  const [busy, setBusy] = useState(false)

  const storedByKeyId = new Map(properties.map((property) => [property.keyId, property.value]))
  const rows = properties.map((property) => ({
    ...property,
    draft: drafts[property.keyId] ?? property.value,
    storedValue: property.value,
  }))
  const tooLong = rows.filter((row) => row.draft.length > MAX_PROPERTY_VALUE_LENGTH)
  const dirty = hasPendingEdits(rows)

  // Keys already on this page are dropped from the picker: setting one again
  // would overwrite the row that is already editable above, which is not what
  // "add a property" means to anyone using it.
  const availableKeys = registry.filter((key) => !storedByKeyId.has(key.id))

  /** Routes a typed mutation error to its designed surface; true when there was one. */
  const surfaceError = (mutationError: Parameters<typeof asReadOnlyReplica>[0]): boolean => {
    const replica = asReadOnlyReplica(mutationError)
    if (replica) {
      onReplicaRefusal(replica.originInstanceId ?? 'its origin instance')
      return true
    }
    const text = describeMutationError(mutationError)
    if (text) {
      onFeedback({ notice: null, error: text })
      return true
    }
    return false
  }

  const handleSave = async () => {
    if (!dirty || busy || tooLong.length > 0) return
    onFeedback({ notice: null, error: null })
    setBusy(true)
    try {
      let set = 0
      let removed = 0
      for (const row of rows) {
        const intent = resolvePropertyEdit(row.draft, row.storedValue)
        // A cleared field means "this page no longer has this property", so
        // it becomes removePageProperty — setPageProperty with "" is refused
        // by design (design.md §20: one mutation, one meaning), and showing
        // that refusal here would be the UI reporting a backend rule the
        // user never broke. See properties/propertyEdit.ts.
        if (intent.kind === 'set') {
          const result = await setPageProperty({
            input: { pageId, pagePropertyKeyId: row.keyId, value: intent.value },
          })
          if (result.error !== undefined) {
            onFeedback({ notice: null, error: describeWriteFailure('PAGE_PROPERTY').summary })
            return
          }
          if (surfaceError(result.data?.setPageProperty.error)) return
          set += 1
        } else if (intent.kind === 'remove') {
          const result = await removePageProperty({ input: { pageId, pagePropertyKeyId: row.keyId } })
          if (result.error !== undefined) {
            onFeedback({ notice: null, error: describeWriteFailure('PAGE_PROPERTY').summary })
            return
          }
          if (surfaceError(result.data?.removePageProperty.error)) return
          removed += 1
        }
      }
      const parts = [set > 0 ? `${set} saved` : null, removed > 0 ? `${removed} removed` : null].filter(Boolean)
      onFeedback({ notice: `Properties updated — ${parts.join(', ')}.`, error: null })
    } finally {
      setBusy(false)
      // Always re-read, including after a refusal partway through: some rows
      // may already have been written, and a stale table would misreport what
      // this page now carries.
      onChanged()
    }
  }

  const handleRemove = async (row: PropertyValue) => {
    if (busy) return
    onFeedback({ notice: null, error: null })
    setBusy(true)
    try {
      const result = await removePageProperty({ input: { pageId, pagePropertyKeyId: row.keyId } })
      if (result.error !== undefined) {
        onFeedback({ notice: null, error: describeWriteFailure('PAGE_PROPERTY').summary })
        return
      }
      if (surfaceError(result.data?.removePageProperty.error)) return
      onFeedback({ notice: `Removed ${row.key}.`, error: null })
    } finally {
      setBusy(false)
      onChanged()
    }
  }

  const handleAdd = async () => {
    if (!newKeyId || newValue.trim().length === 0 || newValue.length > MAX_PROPERTY_VALUE_LENGTH || busy) return
    onFeedback({ notice: null, error: null })
    setBusy(true)
    try {
      const result = await setPageProperty({ input: { pageId, pagePropertyKeyId: newKeyId, value: newValue } })
      if (result.error !== undefined) {
        onFeedback({ notice: null, error: describeWriteFailure('PAGE_PROPERTY').summary })
        return
      }
      if (surfaceError(result.data?.setPageProperty.error)) return
      onFeedback({ notice: `Added ${result.data?.setPageProperty.property?.key ?? 'the property'}.`, error: null })
      setNewKeyId('')
      setNewValue('')
    } finally {
      setBusy(false)
      onChanged()
    }
  }

  return (
    <Stack spacing={2}>
      {properties.length === 0 ? (
        // An empty page and an unreachable registry are different facts: the
        // property read succeeded either way, so this stays an empty state
        // and never borrows the failure's words (see web/README.md).
        <Typography color="text.secondary">
          {!canEdit
            ? 'No properties on this page — only someone who can edit it can add them.'
            : registryUnavailable
              ? 'No properties yet — the key registry is unreachable, so there is nothing to pick from right now.'
              : registry.length === 0
                ? 'No properties yet — an instance admin defines the available keys.'
                : 'No properties yet — pick a key below to add the first one.'}
        </Typography>
      ) : (
        <TableContainer component={Paper} variant="outlined" sx={{ maxWidth: 880 }}>
          <Table size="small" aria-label="Page properties">
            <TableHead>
              <TableRow>
                <TableCell sx={{ width: '30%' }}>Key</TableCell>
                <TableCell>Value</TableCell>
                {canEdit && (
                  <TableCell sx={{ width: 64 }}>
                    <Box component="span" sx={visuallyHidden}>
                      Actions
                    </Box>
                  </TableCell>
                )}
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map((row) => {
                const over = row.draft.length > MAX_PROPERTY_VALUE_LENGTH
                return (
                  <TableRow key={row.keyId}>
                    <TableCell component="th" scope="row" sx={{ fontWeight: 600, verticalAlign: 'top', pt: 2 }}>
                      {row.key}
                    </TableCell>
                    <TableCell>
                      {canEdit ? (
                        <TextField
                          size="small"
                          fullWidth
                          value={row.draft}
                          disabled={busy}
                          error={over}
                          helperText={
                            over
                              ? `A property value may be at most ${MAX_PROPERTY_VALUE_LENGTH} characters.`
                              : undefined
                          }
                          onChange={(e) => setDrafts((prev) => ({ ...prev, [row.keyId]: e.target.value }))}
                          slotProps={{ htmlInput: { 'aria-label': `Value for ${row.key}` } }}
                        />
                      ) : (
                        <Typography variant="body2" sx={{ overflowWrap: 'anywhere' }}>
                          {row.value}
                        </Typography>
                      )}
                    </TableCell>
                    {canEdit && (
                      <TableCell sx={{ verticalAlign: 'top', pt: 1.5 }}>
                        <Tooltip title={`Remove ${row.key}`}>
                          <span>
                            <IconButton
                              size="small"
                              color="error"
                              aria-label={`Remove ${row.key}`}
                              disabled={busy}
                              onClick={() => void handleRemove(row)}
                            >
                              <DeleteOutlinedIcon fontSize="small" />
                            </IconButton>
                          </span>
                        </Tooltip>
                      </TableCell>
                    )}
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        </TableContainer>
      )}

      {canEdit && properties.length > 0 && (
        <Box>
          <Button variant="contained" disabled={!dirty || busy || tooLong.length > 0} onClick={() => void handleSave()}>
            Save changes
          </Button>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
            Clearing a value removes the property from this page.
          </Typography>
        </Box>
      )}

      {canEdit && registryUnavailable && (
        <Alert severity="info">{describeLoadFailure('PROPERTY_KEY_REGISTRY').summary}</Alert>
      )}

      {canEdit && !registryUnavailable && registry.length > 0 && (
        <Paper variant="outlined" sx={{ p: 2, maxWidth: 880 }}>
          <Typography variant="h6" component="h2" sx={{ mb: 1 }}>
            Add a property
          </Typography>
          {availableKeys.length === 0 ? (
            <Typography color="text.secondary">
              This page already carries every defined key — edit the values above, or ask an instance admin for a new
              key.
            </Typography>
          ) : (
            <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start', flexWrap: 'wrap' }}>
              <TextField
                select
                size="small"
                label="Key"
                value={newKeyId}
                disabled={busy}
                onChange={(e) => setNewKeyId(e.target.value)}
                // A key's registry description is guidance for exactly this
                // moment (design.md §20.1), so it replaces the generic line
                // once a key is picked.
                helperText={
                  availableKeys.find((key) => key.id === newKeyId)?.description ??
                  'Keys come from the instance registry.'
                }
                sx={{ minWidth: 220 }}
              >
                {availableKeys.map((key) => (
                  <MenuItem key={key.id} value={key.id}>
                    {key.key}
                  </MenuItem>
                ))}
              </TextField>
              <TextField
                size="small"
                label="Value"
                value={newValue}
                disabled={busy}
                error={newValue.length > MAX_PROPERTY_VALUE_LENGTH}
                helperText={
                  newValue.length > MAX_PROPERTY_VALUE_LENGTH
                    ? `A property value may be at most ${MAX_PROPERTY_VALUE_LENGTH} characters.`
                    : 'Plain text.'
                }
                onChange={(e) => setNewValue(e.target.value)}
                sx={{ minWidth: 320, flexGrow: 1 }}
              />
              <Button
                variant="outlined"
                startIcon={<AddIcon />}
                sx={{ mt: 0.25 }}
                // An empty value is not an "add" gesture at all — the server
                // refuses it (design.md §20), so the button never offers it.
                disabled={
                  busy || !newKeyId || newValue.trim().length === 0 || newValue.length > MAX_PROPERTY_VALUE_LENGTH
                }
                onClick={() => void handleAdd()}
              >
                Add property
              </Button>
            </Stack>
          )}
        </Paper>
      )}
    </Stack>
  )
}

/**
 * A page's details: everything about the page that is not the page.
 *
 * The screen exists so that page management can collect here rather than
 * accreting on the page view. It now holds the marking (§21), properties (§20),
 * the move dialog (§6.4.1) and the way through to page permissions (§6.6).
 * Anything moved here should be metadata ABOUT the page, never content of it.
 *
 * **Open to any viewer, read-only; editors additionally get the controls.**
 * This REVERSES the editors-only gate this screen shipped with (b69a7b7), whose
 * reasoning was that "an editing surface that renders read-only for most of its
 * visitors is a worse answer than one that says who it is for". That was sound
 * while the page view still rendered its own read-only properties panel — a
 * reader had somewhere else to read them. It stopped being sound the moment
 * this became the ONLY place properties live: the gate would then have taken
 * properties away from readers entirely, which is not a tightening, it is a
 * removal. Properties carry no restriction of their own (§20.2) and are
 * readable by anyone who can read the page, so the screen follows the data.
 *
 * The gates are therefore per SECTION, not per screen, and they genuinely
 * differ:
 *  - marking + properties: read-only without `canEdit`, editable with it. Both
 *    components already take a `canEdit` prop and render both ways, so nothing
 *    branches around them.
 *  - move: `canEdit`, matching the source-side requirement the server enforces.
 *  - permissions: `canManageAccess` — instance-admin OR this space's own
 *    space-admin. That is NOT `canEdit`, and the difference is the point: an
 *    instance admin holding no editor grant, and any space-admin on a replica
 *    (where `canEdit` is false for everyone, §12), have `canManageAccess`
 *    without `canEdit`. Hanging permissions off the edit flag would lock out
 *    exactly the people the screen is for.
 *
 * §6.7 is untouched: a page this caller cannot view still returns null and
 * renders the same "couldn't load" notice a nonexistent one does.
 */
export function PageDetailsPage() {
  const { pageId } = useParams<{ pageId: string }>()
  const [{ data, fetching, error }, refetch] = usePagePropertiesForPageQuery({
    variables: { id: pageId ?? '' },
    pause: !pageId,
  })
  // Vocabulary only (§20.1): readable by any authenticated user, so it is
  // fetched for viewers too — the picker it feeds is still canEdit-gated.
  const [{ data: keyData, error: keyError }] = usePagePropertyKeysQuery()
  // design.md §12's proactive banner, same lookup the page view makes: on a
  // replica `canEdit` is already false, so without this the screen would go
  // read-only with no reason given.
  const [{ data: spaceMeta }] = useSpaceReplicaBannerQuery({
    variables: { key: data?.page?.spaceKey ?? '' },
    pause: !data?.page?.spaceKey,
  })
  const [feedback, setFeedback] = useState<Feedback>({ notice: null, error: null })
  const [replicaOrigin, setReplicaOrigin] = useState<string | null>(null)
  const [moveOpen, setMoveOpen] = useState(false)
  useDocumentTitle(data?.page ? `Details — ${data.page.title}` : 'Details')

  // The move dialog's destination tree, with the restriction markers its
  // visibility warning is built from (design.md §6.4). Only fetched for someone
  // who can actually move the page.
  const [{ data: treeData }] = useSpaceTreeForMoveQuery({
    variables: { spaceId: data?.page?.spaceId ?? '' },
    pause: !data?.page?.spaceId || data?.page?.canEdit !== true,
  })
  const [, movePage] = useMovePageMutation()

  // Readable pages only: a page this caller cannot read arrives as a
  // placeholder with no id, and nothing can be moved under it.
  const moveTree = useMemo(() => readableTree(treeData?.pageTree ?? []), [treeData])
  const targetOptions = useMemo(
    () => (pageId ? flattenMoveTargets(moveTree, pageId) : []),
    [moveTree, pageId],
  )
  // The "before" side of the move dialog's visibility warning: what this page
  // currently inherits from its ancestor chain.
  const currentAncestorRestrictions = useMemo(
    () => (pageId ? ancestorRestrictionsOf(moveTree, pageId) : []),
    [moveTree, pageId],
  )
  const sortOrders = useMemo(() => nextSortOrderByTarget(moveTree), [moveTree])

    // FIRST LOAD ONLY. urql retains `data` across a refetch and flips `fetching`
  // true (urql.js computeNextState), so a bare `if (fetching)` threw the screen
  // away on every post-write refetch: content, scroll position and keyboard
  // focus all went with it. `&& !data` keeps the rendered screen up while the
  // re-read happens underneath it.
  if (fetching && !data) {
    return (
      <Stack spacing={1}>
        <Skeleton variant="text" width="40%" height={48} />
        <Skeleton variant="rectangular" height={160} />
      </Stack>
    )
  }

  if (error || !data?.page) {
    // Absent and not-viewable-to-you stay indistinguishable (design.md §6.7).
    return <Alert severity="info">{describeLoadFailure('PAGE').summary}</Alert>
  }

  const page = data.page

  const registry = keyData?.pagePropertyKeys ?? []
  const replicaSpace = spaceMeta?.space?.isReplica === true ? spaceMeta.space : null

  // Nothing to offer beyond the header: no properties, no marking control, no
  // move, no permissions. Worth saying out loud rather than rendering a bare
  // heading over blank space.
  const hasAnything = page.properties.length > 0 || page.canEdit || page.canManageAccess

  return (
    <Stack spacing={2}>
      <PageHeader
        title="Details"
        subject={{ label: page.title, to: `/pages/${page.id}` }}
        description="Everything kept beside this page rather than written into it."
      />

      {/* On a replica `canEdit` is false for EVERYONE (§12 refuses beneath
          every grant), so without this the screen would simply render
          read-only and a space's own editors would be left to infer they had
          been demoted. "This is a replica" and "you are not an editor" are
          different facts, and the gate this screen used to have conflated them
          behind one sentence. */}
      {replicaSpace && (
        <Alert severity="info">
          {replicaBadgeLabel(replicaSpace.originInstanceId)}. {REPLICA_EXPLANATION}
        </Alert>
      )}

      {feedback.error && (
        <Alert severity="warning" onClose={() => setFeedback({ notice: null, error: null })}>
          {feedback.error}
        </Alert>
      )}

      {/* design.md §21: markings sit on this screen because it is already the
          "everything about this page that is not its text" screen — but above
          the table and in their own bordered section, never as a row. A
          property is metadata beside the page (§20); a marking decides who may
          read the page at all, and a control that looked like a key/value row
          would say otherwise. Remounted on change for the same draft
          re-baselining reason as the table below. */}
      <PageMarkingSection
        key={[
          page.marking.level,
          page.marking.eyesOnly.join(','),
          page.marking.ukPrefix ? 'UK' : '',
          page.marking.selectors.map((selector) => `${selector.category}=${selector.value}`).join(','),
        ].join('|')}
        pageId={page.id}
        spaceKey={page.spaceKey}
        marking={page.marking}
        canEdit={page.canEdit}
        onFeedback={setFeedback}
        onReplicaRefusal={setReplicaOrigin}
        onChanged={() => refetch({ requestPolicy: 'network-only' })}
      />

      <Divider />

      <Box>
        <Typography variant="h6" component="h2">
          Properties
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
          Key/value metadata — properties stay out of the page text, so they are also invisible to search and are not
          carried by a Markdown export.
        </Typography>
      </Box>

      <PropertiesEditor
        // Remount after a refetch so the drafts re-baseline against fresh
        // server rows (same idiom as PagePermissionsPage's restriction rows).
        key={page.properties.map((property) => `${property.keyId}:${property.value}`).join('|')}
        pageId={page.id}
        properties={page.properties}
        registry={registry}
        registryUnavailable={keyError !== undefined}
        canEdit={page.canEdit}
        onFeedback={setFeedback}
        onReplicaRefusal={setReplicaOrigin}
        onChanged={() => refetch({ requestPolicy: 'network-only' })}
      />

      {/* Where the page sits, and who may read it — the two management actions
          that used to be buried in the page view's overflow menu. Separately
          gated: see this component's doc comment on why permissions cannot
          ride on `canEdit`. */}
      {(page.canEdit || page.canManageAccess) && (
        <>
          <Divider />
          <Box>
            <Typography variant="h6" component="h2">
              Placement and access
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
              Where this page sits in the space, and the rules deciding who can reach it.
            </Typography>
          </Box>
          <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap' }}>
            {page.canEdit && (
              <Button
                startIcon={<DriveFileMoveOutlinedIcon />}
                variant="outlined"
                size="small"
                onClick={() => setMoveOpen(true)}
              >
                Move page
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
          </Stack>
        </>
      )}

      {!hasAnything && (
        <Typography color="text.secondary">
          Nothing is kept beside this page yet, and managing what could be needs edit permission on it.
        </Typography>
      )}

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
            const replica = asReadOnlyReplica(result.data?.movePage.error)
            if (replica) {
              setReplicaOrigin(replica.originInstanceId ?? 'its origin instance')
              return
            }
            const refused = describeMutationError(result.data?.movePage.error)
            if (refused) {
              setFeedback({ notice: null, error: refused })
              return
            }
            setFeedback({ notice: `Moved "${page.title}".`, error: null })
            refetch({ requestPolicy: 'network-only' })
          })
        }}
      />

      <Snackbar
        open={feedback.notice !== null}
        autoHideDuration={SNACKBAR_AUTO_HIDE_MS}
        onClose={() => setFeedback((prev) => ({ ...prev, notice: null }))}
      >
        <Alert severity="success" onClose={() => setFeedback((prev) => ({ ...prev, notice: null }))}>
          {feedback.notice}
        </Alert>
      </Snackbar>

      <ReadOnlyReplicaDialog
        open={replicaOrigin !== null}
        originInstanceId={replicaOrigin}
        onClose={() => setReplicaOrigin(null)}
      />
    </Stack>
  )
}

/**
 * `/pages/{id}/properties` → `/pages/{id}/details`. Properties moved into the
 * details screen; this keeps the old address working for anything that already
 * links to it. `replace` so the dead URL does not sit in history waiting for a
 * back button to land on it again.
 */
export function PropertiesRedirect() {
  const { pageId } = useParams<{ pageId: string }>()
  return <Navigate to={`/pages/${pageId}/details`} replace />
}
