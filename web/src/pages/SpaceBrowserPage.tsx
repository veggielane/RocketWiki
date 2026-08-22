import { useMemo, useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import {
  Alert,
  Autocomplete,
  Box,
  Breadcrumbs,
  Button,
  Chip,
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
import HistoryEduOutlinedIcon from '@mui/icons-material/HistoryEduOutlined'
import DriveFileRenameOutlineIcon from '@mui/icons-material/DriveFileRenameOutline'
import ArchiveOutlinedIcon from '@mui/icons-material/ArchiveOutlined'
import { useArchiveSpaceMutation, useRenameSpaceMutation, useSpaceTreeQuery } from '../graphql/generated/graphql'
import { filterTreeByLabel } from '../labels/filterTreeByLabel'

/**
 * The generated query type only nests as deep as the `.graphql` operation
 * asked for (3 levels), so a genuinely recursive tree component needs its
 * own recursive shape rather than one extracted from the query result —
 * the deepest selected level structurally satisfies this via its optional
 * `children`/`labels`.
 */
interface PageTreeNode {
  id: string
  title: string
  labels?: string[]
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

export function SpaceBrowserPage() {
  const { spaceKey } = useParams<{ spaceKey: string }>()
  const navigate = useNavigate()
  const [{ data, fetching, error }, refetch] = useSpaceTreeQuery({ variables: { key: spaceKey ?? '' }, pause: !spaceKey })
  const [, renameSpace] = useRenameSpaceMutation()
  const [, archiveSpace] = useArchiveSpaceMutation()
  const [labelFilter, setLabelFilter] = useState<string | null>(null)
  const [renameOpen, setRenameOpen] = useState(false)
  const [renameValue, setRenameValue] = useState('')
  const [archiveOpen, setArchiveOpen] = useState(false)

  const matches = useMemo(() => {
    if (!labelFilter || !data?.space) return null
    return filterTreeByLabel(
      data.space.tree.map((n) => ({ ...n, labels: n.labels })),
      labelFilter,
    )
  }, [data, labelFilter])

  if (fetching) {
    return <Skeleton variant="rectangular" height={300} />
  }

  if (error || !data?.space) {
    return <Alert severity="info">Couldn't load this space — there's no live API in this environment yet.</Alert>
  }

  const space = data.space

  return (
    <Stack spacing={2}>
      <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <Typography variant="h4" component="h1">
            {space.name}
          </Typography>
          {space.isReplica && (
            <Chip label={`Mirrored from ${space.originInstanceId ?? 'origin'} — read-only`} color="default" />
          )}
        </Stack>
        <Stack direction="row" spacing={1}>
          {/* No client-side gate here — the trash query itself is
              permission-filtered server-side, same "let the server decide
              what's visible" approach as everywhere else, rather than
              guessing who should see a Trash link. */}
          <Button component={RouterLink} to={`/spaces/${space.key}/trash`} startIcon={<DeleteOutlinedIcon />} variant="outlined" size="small">
            Trash
          </Button>
          {space.canManageAccess && (
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
          <Button component={RouterLink} to={`/spaces/${space.key}/import-report`} startIcon={<HistoryEduOutlinedIcon />} variant="outlined" size="small">
            Import report
          </Button>
          {/* design.md §6.5.1: rename/archive require instance admin OR
              this space's own space-admin — same canManageAccess union
              used everywhere else, not a separate check. */}
          {space.canManageAccess && (
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
          {space.canManageAccess && (
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

      {space.labels.length > 0 && (
        <Autocomplete
          size="small"
          options={space.labels}
          value={labelFilter}
          onChange={(_e, value) => setLabelFilter(value)}
          renderInput={(params) => <TextField {...params} label="Filter by label" />}
          sx={{ maxWidth: 280 }}
        />
      )}

      {matches ? (
        <Stack spacing={1}>
          {matches.length === 0 && (
            <Typography variant="body2" color="text.secondary">
              No pages labeled "{labelFilter}".
            </Typography>
          )}
          {matches.map((match) => (
            <Stack key={match.id} spacing={0}>
              {match.path.length > 0 && (
                <Breadcrumbs separator="›" sx={{ fontSize: '0.75rem' }}>
                  {match.path.map((title, i) => (
                    <Typography key={i} variant="caption" color="text.secondary">
                      {title}
                    </Typography>
                  ))}
                </Breadcrumbs>
              )}
              <ListItemButton component={RouterLink} to={`/pages/${match.id}`} disableGutters sx={{ pl: 0 }}>
                <ListItemText primary={match.title} />
              </ListItemButton>
            </Stack>
          ))}
        </Stack>
      ) : (
        <Box role="region" aria-label="Page tree">
          <PageTreeList nodes={space.tree} />
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
              await renameSpace({ input: { spaceKey: space.key, newName: renameValue.trim() } })
              setRenameOpen(false)
              refetch({ requestPolicy: 'network-only' })
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
            The space and its pages become read-only and disappear from the active spaces list. It can be restored
            later from Archived spaces.
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
              await archiveSpace({ input: { spaceKey: space.key } })
              setArchiveOpen(false)
              navigate('/')
            }}
          >
            Archive
          </Button>
        </DialogActions>
      </Dialog>
    </Stack>
  )
}
