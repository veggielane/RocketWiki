import { useState } from 'react'
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
  List,
  ListItemButton,
  ListItemText,
  Skeleton,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import ShieldOutlinedIcon from '@mui/icons-material/ShieldOutlined'
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined'
import DriveFileRenameOutlineIcon from '@mui/icons-material/DriveFileRenameOutline'
import ArchiveOutlinedIcon from '@mui/icons-material/ArchiveOutlined'
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
  children?: PageTreeNode[]
}

function PageTreeList({ nodes, depth = 0 }: { nodes: PageTreeNode[]; depth?: number }) {
  return (
    <List dense disablePadding>
      {nodes.map((node) => (
        <li key={node.id}>
          <ListItemButton component={RouterLink} to={`/pages/${node.id}`} sx={{ pl: 2 + depth * 2 }}>
            <ListItemText primary={node.title} />
          </ListItemButton>
          {node.children && node.children.length > 0 && (
            <PageTreeList nodes={node.children} depth={depth + 1} />
          )}
        </li>
      ))}
    </List>
  )
}

/**
 * NOTE (schema reconciliation): three placeholder-era affordances are gone
 * because the real schema carries no data for them (each reported as a
 * contract gap): the replica banner (no client-usable isReplica), the
 * label filter facet (PageTreeNode has no labels), and the import-report
 * link (no importReports query — the importer is milestone 5, not
 * started). Space management is gated on `grants` being non-empty: the
 * server returns grant rows only to instance/space admins ("absent, not
 * forbidden"), and a space always has at least one grant by construction,
 * so an empty list means "not yours to manage".
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
  // No read path for "am I watching?" (reported contract gap) — the toggle
  // reflects only what this visit did, same as the page-level watch button.
  const [watching, setWatching] = useState<boolean | null>(null)

  if (fetching) {
    return <Skeleton variant="rectangular" height={300} />
  }

  if (error || !data?.space) {
    return <Alert severity="info">Couldn't load this space.</Alert>
  }

  const space = data.space
  const canManage = space.grants.length > 0

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
    if (watching === true) {
      const result = await unwatchSpace({ input: { spaceId: space.id } })
      if (!surfaceError(result.data?.unwatchSpace.error)) setWatching(false)
      return
    }
    // design.md §8: watching a space is how a user hears that a sync
    // bundle changed it — deliberately allowed on replica spaces too (a
    // Watch row is instance-local user metadata, not a replica write).
    const result = await watchSpace({ input: { spaceId: space.id } })
    if (!surfaceError(result.data?.watchSpace.error)) setWatching(true)
  }

  return (
    <Stack spacing={2}>
      <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="h4" component="h1">
          {space.name}
        </Typography>
        <Stack direction="row" spacing={1}>
          <Button
            startIcon={watching === true ? <NotificationsActiveIcon /> : <NotificationsNoneOutlinedIcon />}
            variant="outlined"
            size="small"
            onClick={() => void handleToggleWatch()}
            aria-pressed={watching === true}
          >
            {watching === true ? 'Watching' : 'Watch'}
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

      {actionError && (
        <Alert severity="warning" onClose={() => setActionError(null)}>
          {actionError}
        </Alert>
      )}

      <Box role="region" aria-label="Page tree">
        <PageTreeList nodes={treeData?.pageTree ?? []} />
      </Box>

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
