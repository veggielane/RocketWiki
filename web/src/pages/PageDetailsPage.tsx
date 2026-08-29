import { useState } from 'react'
import { Navigate, useParams } from 'react-router-dom'
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
import {
  usePagePropertiesForPageQuery,
  usePagePropertyKeysQuery,
  useRemovePagePropertyMutation,
  useSetPagePropertyMutation,
  useSpaceReplicaBannerQuery,
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
 * Properties (design.md §20) are its first section and, for now, its only one —
 * the screen exists so that more can move here rather than accreting on the page
 * view, which is what it was already starting to do. Anything moved here should
 * be metadata ABOUT the page, never content of it.
 *
 * **Editors only**, which is a deliberate tightening. The properties screen this
 * replaces was open to anyone with canView, on the reasoning that properties
 * carry no restriction of their own (§20.2) — still true, and the values remain
 * visible to every reader on the page view. What changes is that the screen for
 * MANAGING them is now an editing surface, and an editing surface that renders
 * read-only for most of its visitors is a worse answer than one that says who it
 * is for. The server is unmoved either way: every write is still gated on
 * canEdit, and this only decides what to offer.
 *
 * Replica spaces refuse every value write beneath every grant (§12) — that
 * refusal renders as the shared replica explainer, never a raw toast.
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
  useDocumentTitle(data?.page ? `Details — ${data.page.title}` : 'Details')

  if (fetching) {
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

  // Editors only. Not a router guard: `canEdit` is server-computed and arrives with
  // the page, so gating here needs no second round trip and no duplicated rule — and
  // someone following a stale link gets a sentence rather than a blank. The server
  // refuses every write regardless; this only decides what to offer.
  //
  // The replica arm is not a nicety. On a replica `canEdit` is false for EVERYONE
  // (§12 refuses beneath every grant), so a bare canEdit gate would tell a space's
  // own editors they are not editors — true of the flag, false of them, and it would
  // also swallow the one screen that explains why the space is read-only.
  if (!page.canEdit) {
    return (
      <Alert severity="info">
        {replicaSpace
          ? `${replicaBadgeLabel(replicaSpace.originInstanceId)} — ${REPLICA_EXPLANATION} Its details cannot be changed here.`
          : "A page's details are managed by its editors. You can still read this page, and its properties, from the page itself."}
      </Alert>
    )
  }

  return (
    <Stack spacing={2}>
      <PageHeader
        title="Details"
        subject={{ label: page.title, to: `/pages/${page.id}` }}
        description="Everything kept beside this page rather than written into it."
      />

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
        key={`${page.marking.level}|${page.marking.eyesOnly.join(',')}|${page.marking.prefix ?? ''}`}
        pageId={page.id}
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
