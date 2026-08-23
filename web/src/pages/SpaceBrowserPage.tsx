import { useMemo, useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import {
  Alert,
  Autocomplete,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  List,
  ListItemButton,
  ListItemText,
  Skeleton,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import ShieldOutlinedIcon from '@mui/icons-material/ShieldOutlined'
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined'
import DriveFileRenameOutlineIcon from '@mui/icons-material/DriveFileRenameOutline'
import ArchiveOutlinedIcon from '@mui/icons-material/ArchiveOutlined'
import LockOutlinedIcon from '@mui/icons-material/LockOutlined'
import NotificationsNoneOutlinedIcon from '@mui/icons-material/NotificationsNoneOutlined'
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive'
import {
  useArchiveSpaceMutation,
  useRenameSpaceMutation,
  useSpaceTreeQuery,
  useSpacePageTreeQuery,
  useWatchSpaceMutation,
  useUnwatchSpaceMutation,
} from '../graphql/generated/graphql'
import { asReadOnlyReplica, describeMutationError } from '../graphql/mutationError'
import { filterTreeByLabel } from '../labels/filterTreeByLabel'
import { ReadOnlyReplicaDialog } from './ReadOnlyReplicaDialog'

/**
 * The generated query type only nests as deep as the `.graphql` operation
 * asked for (4 levels), so a genuinely recursive tree component needs its
 * own recursive shape rather than one extracted from the query result —
 * the deepest selected level structurally satisfies this via its optional
 * `children`.
 */
interface PageTreeNode {
  id: string
  title: string
  /** `PageTreeNode.hasRestrictions` — drives the lock badge (design.md §6.6). */
  hasRestrictions: boolean
  labels: string[]
  children?: PageTreeNode[]
}

function PageTreeList({ nodes, depth = 0 }: { nodes: PageTreeNode[]; depth?: number }) {
  return (
    <List dense disablePadding>
      {nodes.map((node) => (
        <li key={node.id}>
          <ListItemButton component={RouterLink} to={`/pages/${node.id}`} sx={{ pl: 2 + depth * 2 }}>
            <ListItemText primary={node.title} />
            {node.hasRestrictions && (
              <Tooltip title="This page has access restrictions">
                <LockOutlinedIcon fontSize="small" color="action" aria-label="Has access restrictions" />
              </Tooltip>
            )}
          </ListItemButton>
          {node.children && node.children.length > 0 && (
            <PageTreeList nodes={node.children} depth={depth + 1} />
          )}
        </li>
      ))}
    </List>
  )
}

function distinctLabels(nodes: PageTreeNode[]): string[] {
  const labels = new Set<string>()
  function walk(list: PageTreeNode[]): void {
    for (const node of list) {
      for (const label of node.labels) labels.add(label)
      if (node.children) walk(node.children)
    }
  }
  walk(nodes)
  return [...labels].sort((a, b) => a.localeCompare(b))
}

/**
 * Space browser: page tree with restriction lock badges
 * (`PageTreeNode.hasRestrictions`, design.md §6.6), a label-filter facet
 * (`PageTreeNode.labels` + labels/filterTreeByLabel.ts), and a proactive
 * replica banner (`Space.isReplica`, design.md §12). Space management is
 * gated on `grants` being non-empty: the server returns grant rows only to
 * instance/space admins ("absent, not forbidden"), and a space always has
 * at least one grant by construction, so an empty list means "not yours to
 * manage". The import-report link is deliberately absent until the
 * importer (milestone 5) exists to produce reports.
 */
export function SpaceBrowserPage() {
  const { spaceKey } = useParams<{ spaceKey: string }>()
  const navigate = useNavigate()
  const [{ data, fetching, error }, refetch] = useSpaceTreeQuery({ variables: { key: spaceKey ?? '' }, pause: !spaceKey })
  const spaceId = data?.space?.id
  const [{ data: treeData }] = useSpacePageTreeQuery({ variables: { spaceId: spaceId ?? '' }, pause: !spaceId })
  const [, renameSpace] = useRenameSpaceMutation()
  const [, archiveSpace] = useArchiveSpaceMutation()
  const [, watchSpace] = useWatchSpaceMutation()
  const [, unwatchSpace] = useUnwatchSpaceMutation()
  const [renameOpen, setRenameOpen] = useState(false)
  const [renameValue, setRenameValue] = useState('')
  const [archiveOpen, setArchiveOpen] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)
  const [replicaOrigin, setReplicaOrigin] = useState<string | null>(null)
  const [labelFilter, setLabelFilter] = useState<string | null>(null)
  // Server truth (`viewerIsWatching`) with an optimistic override on click,
  // reverted if the mutation is refused; reset when switching spaces.
  const [watchOverride, setWatchOverride] = useState<boolean | null>(null)
  // Switching spaces must not carry per-space UI state across — same
  // render-time reset idiom as AuditLogPage's filter-key comparison.
  const [stateSpaceKey, setStateSpaceKey] = useState(spaceKey)
  if (stateSpaceKey !== spaceKey) {
    setStateSpaceKey(spaceKey)
    setWatchOverride(null)
    setLabelFilter(null)
  }

  const tree: PageTreeNode[] = useMemo(() => treeData?.pageTree ?? [], [treeData])
  const availableLabels = useMemo(() => distinctLabels(tree), [tree])
  const labelMatches = useMemo(() => (labelFilter ? filterTreeByLabel(tree, labelFilter) : []), [tree, labelFilter])

  if (fetching) {
    return <Skeleton variant="rectangular" height={300} />
  }

  if (error || !data?.space) {
    return <Alert severity="info">Couldn't load this space.</Alert>
  }

  const space = data.space
  const canManage = space.grants.length > 0
  const watching = watchOverride ?? space.viewerIsWatching

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

  const handleToggleWatch = async () => {
    setActionError(null)
    const next = !watching
    setWatchOverride(next) // optimistic — reverted below if refused
    if (!next) {
      const result = await unwatchSpace({ input: { spaceId: space.id } })
      if (result.error !== undefined || surfaceError(result.data?.unwatchSpace.error)) setWatchOverride(!next)
      return
    }
    // design.md §8: watching a space is how a user hears that a sync
    // bundle changed it — deliberately allowed on replica spaces too (a
    // Watch row is instance-local user metadata, not a replica write).
    const result = await watchSpace({ input: { spaceId: space.id } })
    if (result.error !== undefined || surfaceError(result.data?.watchSpace.error)) setWatchOverride(!next)
  }

  return (
    <Stack spacing={2}>
      <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="h4" component="h1">
          {space.name}
        </Typography>
        <Stack direction="row" spacing={1}>
          <Button
            startIcon={watching ? <NotificationsActiveIcon /> : <NotificationsNoneOutlinedIcon />}
            variant="outlined"
            size="small"
            onClick={() => void handleToggleWatch()}
            aria-pressed={watching}
          >
            {watching ? 'Watching' : 'Watch'}
          </Button>
          {/* No client-side gate here — the trash query itself is
              permission-filtered server-side, same "let the server decide
              what's visible" approach as everywhere else, rather than
              guessing who should see a Trash link. */}
          <Button component={RouterLink} to={`/spaces/${space.key}/trash`} startIcon={<DeleteOutlinedIcon />} variant="outlined" size="small">
            Trash
          </Button>
          {canManage && (
            <Button
              component={RouterLink}
              to={`/spaces/${space.key}/grants`}
              startIcon={<ShieldOutlinedIcon />}
              variant="outlined"
              size="small"
            >
              Grants
            </Button>
          )}
          {/* design.md §6.5.1: rename/archive require instance admin OR
              this space's own space-admin — the same server-computed
              signal (non-empty grants) gates both. */}
          {canManage && (
            <Button
              startIcon={<DriveFileRenameOutlineIcon />}
              variant="outlined"
              size="small"
              onClick={() => {
                setRenameValue(space.name)
                setRenameOpen(true)
              }}
            >
              Rename
            </Button>
          )}
          {canManage && (
            <Button
              startIcon={<ArchiveOutlinedIcon />}
              variant="outlined"
              color="warning"
              size="small"
              onClick={() => setArchiveOpen(true)}
            >
              Archive
            </Button>
          )}
        </Stack>
      </Stack>

      {/* design.md §12: proactive replica banner — not just the reactive
          dialog after a refused write. */}
      {space.isReplica && (
        <Alert severity="info">
          Mirrored from {space.originInstanceId} — read-only. Content arrives via one-way sync; editing happens on
          the origin instance (design.md §12).
        </Alert>
      )}

      {actionError && (
        <Alert severity="warning" onClose={() => setActionError(null)}>
          {actionError}
        </Alert>
      )}

      {availableLabels.length > 0 && (
        <Autocomplete
          options={availableLabels}
          value={labelFilter}
          onChange={(_e, value) => setLabelFilter(value)}
          size="small"
          sx={{ maxWidth: 320 }}
          renderInput={(params) => <TextField {...params} label="Filter by label" />}
        />
      )}

      {labelFilter ? (
        <Box role="region" aria-label={`Pages labelled ${labelFilter}`}>
          {labelMatches.length === 0 ? (
            <Typography color="text.secondary">No pages carry this label.</Typography>
          ) : (
            <List dense disablePadding>
              {labelMatches.map((match) => (
                <ListItemButton key={match.id} component={RouterLink} to={`/pages/${match.id}`}>
                  <ListItemText
                    primary={match.title}
                    secondary={match.path.length > 0 ? match.path.join(' / ') : undefined}
                  />
                </ListItemButton>
              ))}
            </List>
          )}
        </Box>
      ) : (
        <Box role="region" aria-label="Page tree">
          <PageTreeList nodes={tree} />
        </Box>
      )}

      <Dialog open={renameOpen} onClose={() => setRenameOpen(false)}>
        <DialogTitle>Rename "{space.name}"</DialogTitle>
        <DialogContent>
          <TextField
            autoFocus
            label="Name"
            value={renameValue}
            onChange={(e) => setRenameValue(e.target.value)}
            fullWidth
            sx={{ mt: 1 }}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setRenameOpen(false)}>Cancel</Button>
          <Button
            variant="contained"
            disabled={renameValue.trim().length === 0}
            onClick={async () => {
              const result = await renameSpace({
                // Description passed through unchanged — the input replaces
                // it wholesale, so omitting it would clear it.
                input: { spaceId: space.id, name: renameValue.trim(), description: space.description },
              })
              setRenameOpen(false)
              if (!surfaceError(result.data?.renameSpace.error)) {
                refetch({ requestPolicy: 'network-only' })
              }
            }}
          >
            Rename
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog open={archiveOpen} onClose={() => setArchiveOpen(false)}>
        <DialogTitle>Archive "{space.name}"?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            The space and its pages become read-only and disappear from the active spaces list.
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button autoFocus onClick={() => setArchiveOpen(false)}>
            Cancel
          </Button>
          <Button
            color="warning"
            variant="contained"
            onClick={async () => {
              const result = await archiveSpace({ input: { spaceId: space.id } })
              setArchiveOpen(false)
              if (!surfaceError(result.data?.archiveSpace.error)) {
                navigate('/')
              }
            }}
          >
            Archive
          </Button>
        </DialogActions>
      </Dialog>

      <ReadOnlyReplicaDialog
        open={replicaOrigin !== null}
        originInstanceId={replicaOrigin}
        onClose={() => setReplicaOrigin(null)}
      />
    </Stack>
  )
}
