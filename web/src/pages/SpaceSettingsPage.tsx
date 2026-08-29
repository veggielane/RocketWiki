import { useState } from 'react'
import { useNavigate, useParams, Link as RouterLink } from 'react-router-dom'
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Divider,
  List,
  ListItem,
  MenuItem,
  ListItemButton,
  ListItemText,
  Paper,
  Skeleton,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import AccountTreeOutlinedIcon from '@mui/icons-material/AccountTreeOutlined'
import InsightsOutlinedIcon from '@mui/icons-material/InsightsOutlined'
import ShieldOutlinedIcon from '@mui/icons-material/ShieldOutlined'
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined'
import ArchiveOutlinedIcon from '@mui/icons-material/ArchiveOutlined'
import {
  useArchiveSpaceMutation,
  useRenameSpaceMutation,
  useSetSpaceHomepageMutation,
  useSpacePageTreeQuery,
  useSpaceTreeQuery,
} from '../graphql/generated/graphql'
import { asReadOnlyReplica, describeMutationError } from '../graphql/mutationError'
import { describeLoadFailure, REPLICA_EXPLANATION, replicaBadgeLabel } from '../feedback/unavailableCopy'
import { ReadOnlyReplicaDialog } from '../feedback/ReadOnlyReplicaDialog'
import { flattenParentOptions } from './parentOptions'
import { PAGE_TREE_CONTEXT } from '../graphql/treeDependencies'

/**
 * The "no default page" choice. A sentinel rather than `''` for the same reason
 * the icon picker uses one: MUI reads an empty Select value as unfilled and
 * leaves the floating label sitting on the option text. Lowercase and bracketed
 * so it can never collide with a page id.
 */
const NO_HOMEPAGE = '__none__'

/**
 * Everything that manages one space, in one place (design.md §6.5.1): its name
 * and description, the way through to its grants and trash, and archiving.
 *
 * Gated the same way the space browser gates its management affordances —
 * `grants` non-empty. The server returns grant rows only to instance admins and
 * that space's own space-admins ("absent, not forbidden"), and a space always
 * has at least one grant by construction, so an empty list means "not yours to
 * manage" without the client having to guess. The server re-checks every
 * mutation regardless; this only decides what to offer.
 *
 * The key is deliberately not editable. It is in every URL and every sync
 * bundle's identity, so renaming it is a migration rather than a setting.
 */
export function SpaceSettingsPage() {
  const { spaceKey } = useParams<{ spaceKey: string }>()
  const navigate = useNavigate()
  const [{ data, fetching, error }, refetch] = useSpaceTreeQuery({
    variables: { key: spaceKey ?? '' },
    pause: !spaceKey,
  })
  const [, renameSpace] = useRenameSpaceMutation()
  const [, archiveSpace] = useArchiveSpaceMutation()
  const [, setSpaceHomepage] = useSetSpaceHomepageMutation()

  // The space's own tree is what the default page is chosen FROM, and the
  // server refuses a page from anywhere else — so offering the tree is not a
  // convenience, it is the only set that can succeed.
  const [{ data: treeData }] = useSpacePageTreeQuery({
    variables: { spaceId: data?.space?.id ?? '' },
    pause: !data?.space?.id,
    context: PAGE_TREE_CONTEXT,
  })

  const [name, setName] = useState<string | null>(null)
  const [description, setDescription] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)
  const [replicaOrigin, setReplicaOrigin] = useState<string | null>(null)
  const [archiveOpen, setArchiveOpen] = useState(false)
  const [homepageDraft, setHomepageDraft] = useState<string | null>(null)

  if (fetching) {
    return <Skeleton variant="rectangular" height={300} />
  }

  if (error || !data?.space) {
    return <Alert severity="info">{describeLoadFailure('SPACE').summary}</Alert>
  }

  const space = data.space
  const canManage = space.grants.length > 0
  // Server values until edited, so the fields track a refetch rather than
  // pinning whatever was loaded when the component first mounted.
  const nameValue = name ?? space.name
  const descriptionValue = description ?? space.description ?? ''
  const dirty = nameValue !== space.name || descriptionValue !== (space.description ?? '')

  // Every page in the space, indented, minus the root entry: flattenParentOptions
  // leads with "(top level)" because a NEW PAGE can hang off the space itself.
  // A default page cannot — the space is the thing being defaulted, so the only
  // "nothing" here is NO_HOMEPAGE.
  const pageOptions = flattenParentOptions(treeData?.pageTree ?? []).filter((option) => option.id !== null)
  const homepageValue = homepageDraft ?? space.homepageId ?? NO_HOMEPAGE
  // A homepage the picker has no row for means the tree has not arrived yet, or
  // the page left the space. Either way the Select must not silently show "None"
  // and then clear it on the next save — so the control waits for the tree.
  const homepageKnown = homepageValue === NO_HOMEPAGE || pageOptions.some((o) => o.id === homepageValue)
  const homepageDirty = homepageDraft !== null && homepageDraft !== (space.homepageId ?? NO_HOMEPAGE)

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

  const handleSave = async () => {
    setActionError(null)
    setSaved(false)
    setSaving(true)
    const result = await renameSpace({
      input: { spaceId: space.id, name: nameValue.trim(), description: descriptionValue.trim() || null },
    })
    if (result.error) {
      setSaving(false)
      setActionError(describeLoadFailure('SPACE').summary)
      return
    }
    if (surfaceError(result.data?.renameSpace.error)) {
      setSaving(false)
      return
    }

    // The homepage is a separate mutation, so one Save issues two writes and
    // the second only when it has something to say. Sequential rather than
    // concurrent: if the details write is refused there is no reason to have
    // sent the other, and the two refusals would race for the same alert.
    if (homepageDirty) {
      const homepageResult = await setSpaceHomepage({
        input: { spaceId: space.id, pageId: homepageValue === NO_HOMEPAGE ? null : homepageValue },
      })
      if (homepageResult.error) {
        setSaving(false)
        setActionError(describeLoadFailure('SPACE').summary)
        return
      }
      if (surfaceError(homepageResult.data?.setSpaceHomepage.error)) {
        setSaving(false)
        return
      }
    }

    setSaving(false)
    // Drop the local edits so the fields fall back to the refetched server
    // values — otherwise a later server-side change would never show through.
    setName(null)
    setDescription(null)
    setHomepageDraft(null)
    setSaved(true)
    refetch({ requestPolicy: 'network-only' })
  }

  const handleArchive = async () => {
    setActionError(null)
    const result = await archiveSpace({ input: { spaceId: space.id } })
    setArchiveOpen(false)
    if (result.error) {
      setActionError(describeLoadFailure('SPACE').summary)
      return
    }
    if (surfaceError(result.data?.archiveSpace.error)) {
      return
    }
    navigate('/')
  }

  return (
    <Stack spacing={3}>
      <Stack spacing={0.5}>
        <Typography variant="h4" component="h1">
          Space settings
        </Typography>
        <Typography variant="body2" color="text.secondary">
          <RouterLink to={`/spaces/${space.key}`}>{space.name}</RouterLink>
        </Typography>
      </Stack>

      {space.isReplica && (
        <Alert severity="info">
          {replicaBadgeLabel(space.originInstanceId)} — {REPLICA_EXPLANATION}
        </Alert>
      )}
      {actionError && <Alert severity="error">{actionError}</Alert>}
      {saved && <Alert severity="success">Space settings saved.</Alert>}
      {!canManage && (
        <Alert severity="info">
          You can read this space, but managing it needs instance admin or this space's own
          space-admin role.
        </Alert>
      )}

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack spacing={2}>
          <Typography variant="h6" component="h2">
            Details
          </Typography>
          <TextField
            label="Name"
            value={nameValue}
            onChange={(e) => setName(e.target.value)}
            disabled={!canManage}
            fullWidth
          />
          <TextField
            label="Key"
            value={space.key}
            disabled
            fullWidth
            helperText="Immutable — it identifies this space in every URL and sync bundle."
          />
          <TextField
            label="Description"
            value={descriptionValue}
            onChange={(e) => setDescription(e.target.value)}
            disabled={!canManage}
            fullWidth
            multiline
            minRows={2}
          />
          <TextField
            select
            label="Default page"
            value={homepageKnown ? homepageValue : NO_HOMEPAGE}
            onChange={(e) => setHomepageDraft(e.target.value)}
            disabled={!canManage || !homepageKnown}
            fullWidth
            helperText={
              homepageKnown
                ? 'Where /spaces/{key} lands. "None" shows the space browser instead.'
                : 'Loading this space’s pages…'
            }
          >
            {/* "None" is an option rather than the absence of one: a space
                without a default page is the ordinary state, and it has to stay
                reachable once one has been chosen. */}
            <MenuItem value={NO_HOMEPAGE}>None</MenuItem>
            {pageOptions.map((option) => (
              <MenuItem key={option.id} value={option.id ?? ''}>
                {/* Non-breaking spaces, not padding — MUI renders the selected
                    option's text into the closed field, where styling-based
                    indentation would be lost (same reason as the parent picker). */}
                {' '.repeat(Math.max(0, option.depth - 1) * 2)}
                {option.title}
              </MenuItem>
            ))}
          </TextField>
          <Stack direction="row" spacing={1}>
            <Button
              variant="contained"
              disabled={!canManage || (!dirty && !homepageDirty) || saving || nameValue.trim().length === 0}
              onClick={() => void handleSave()}
            >
              Save
            </Button>
            <Button
              disabled={(!dirty && !homepageDirty) || saving}
              onClick={() => {
                setName(null)
                setDescription(null)
                setHomepageDraft(null)
              }}
            >
              Discard
            </Button>
          </Stack>
        </Stack>
      </Paper>

      <Paper variant="outlined">
        <Stack spacing={0} sx={{ p: 2, pb: 1 }}>
          <Typography variant="h6" component="h2">
            Access and content
          </Typography>
        </Stack>
        {/* Each row is a <li> wrapping the link, not a bare ListItemButton:
            MUI's List renders a <ul>, and `component={RouterLink}` makes the
            button an <a>, so without the wrapper the list has direct <a>
            children — an axe "list" violation, and the same one the space nav
            shipped once. `disablePadding` keeps the row's own padding the only
            padding. */}
        <List>
          {/* Reachable from here because /spaces/{key} now lands on the default
              page when there is one, and the browser's label filter has no other
              way in. Listed first: it is the only row that is not administration. */}
          <ListItem disablePadding>
            <ListItemButton component={RouterLink} to={`/spaces/${space.key}/-/browse`}>
              <AccountTreeOutlinedIcon fontSize="small" sx={{ mr: 2 }} />
              <ListItemText
                primary="Browse pages"
                secondary="The full page tree, and filtering it by label."
              />
            </ListItemButton>
          </ListItem>
          <ListItem disablePadding>
            <ListItemButton component={RouterLink} to={`/spaces/${space.key}/-/analytics`}>
              <InsightsOutlinedIcon fontSize="small" sx={{ mr: 2 }} />
              <ListItemText
                primary="Analytics"
                secondary="What is being read and edited, over the pages you can see."
              />
            </ListItemButton>
          </ListItem>
          <ListItem disablePadding>
            <ListItemButton component={RouterLink} to={`/spaces/${space.key}/-/grants`}>
              <ShieldOutlinedIcon fontSize="small" sx={{ mr: 2 }} />
              <ListItemText
                primary="Grants"
                secondary="Who holds which role in this space (design.md §6.5)."
              />
            </ListItemButton>
          </ListItem>
          <ListItem disablePadding>
            <ListItemButton component={RouterLink} to={`/spaces/${space.key}/-/trash`}>
              <DeleteOutlinedIcon fontSize="small" sx={{ mr: 2 }} />
              <ListItemText primary="Trash" secondary="Deleted pages, and restoring them." />
            </ListItemButton>
          </ListItem>
        </List>
      </Paper>

      {canManage && (
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Stack spacing={2}>
            <Typography variant="h6" component="h2">
              Lifecycle
            </Typography>
            <Typography variant="body2" color="text.secondary">
              Archiving hides the space and its pages from everyday browsing. It is reversible from
              the archived-spaces list.
            </Typography>
            <Divider />
            <Button
              startIcon={<ArchiveOutlinedIcon />}
              color="warning"
              variant="outlined"
              sx={{ alignSelf: 'flex-start' }}
              onClick={() => setArchiveOpen(true)}
            >
              Archive space
            </Button>
          </Stack>
        </Paper>
      )}

      <Dialog open={archiveOpen} onClose={() => setArchiveOpen(false)}>
        <DialogTitle>Archive "{space.name}"?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            The space and its pages stop appearing in browsing and search. Nothing is deleted, and
            an instance admin can restore it from the archived-spaces list.
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button autoFocus onClick={() => setArchiveOpen(false)}>
            Cancel
          </Button>
          <Button color="warning" variant="contained" onClick={() => void handleArchive()}>
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
