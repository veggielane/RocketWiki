import { useMemo, useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import {
  Alert,
  Autocomplete,
  Box,
  Button,
  List,
  ListItem,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Skeleton,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined'
import SettingsOutlinedIcon from '@mui/icons-material/SettingsOutlined'
import NoteAddOutlinedIcon from '@mui/icons-material/NoteAddOutlined'
import LockOutlinedIcon from '@mui/icons-material/LockOutlined'
import ArticleOutlinedIcon from '@mui/icons-material/ArticleOutlined'
import NotificationsNoneOutlinedIcon from '@mui/icons-material/NotificationsNoneOutlined'
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive'
import {
  useSpaceTreeQuery,
  useSpacePageTreeQuery,
  useCreatePageMutation,
  useWatchSpaceMutation,
  useUnwatchSpaceMutation,
  type ClassificationLevel,
} from '../graphql/generated/graphql'
import { asReadOnlyReplica, describeMutationError } from '../graphql/mutationError'
import { describeLoadFailure, REPLICA_EXPLANATION, replicaBadgeLabel } from '../feedback/unavailableCopy'
import { filterTreeByLabel } from '../labels/filterTreeByLabel'
import { ReadOnlyReplicaDialog } from '../feedback/ReadOnlyReplicaDialog'
import { CreatePageDialog, type CreatePageValues } from './CreatePageDialog'
import { flattenParentOptions } from './parentOptions'
import { lookupPageIcon } from './pageIcons'
import { MarkingLevelBadge } from '../markings/MarkingLevelBadge'

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
  /** `PageTreeNode.icon` — null for most pages; the tree draws its generic glyph then. */
  icon?: string | null
  /** The page's address within its space — /spaces/{key}/{slug}. */
  slug: string
  /** `PageTreeNode.hasRestrictions` — drives the lock badge (design.md §6.6). */
  hasRestrictions: boolean
  labels: string[]
  /**
   * `PageTreeNode.marking` — the value the tree's own pruning walk gated on
   * (design.md §21.9), so the badge shows the marking that decided this node
   * is visible rather than a second lookup that could disagree with it.
   */
  marking: { level: ClassificationLevel; levelName: string }
  children?: PageTreeNode[]
}

function PageTreeList({
  nodes,
  spaceKey,
  depth = 0,
}: {
  nodes: PageTreeNode[]
  spaceKey: string
  depth?: number
}) {
  return (
    <List dense disablePadding>
      {nodes.map((node) => {
        // The page's own icon, or the generic page glyph when it has none —
        // and when it names one this build doesn't know. Every row gets one
        // either way: an icon on only the pages that set one would indent
        // those titles past the rest and read as a second hierarchy.
        const Icon = lookupPageIcon(node.icon)?.Icon ?? ArticleOutlinedIcon
        return (
          <li key={node.id}>
            <ListItemButton
              component={RouterLink}
              to={`/spaces/${spaceKey}/${node.slug}`}
              sx={{ pl: 2 + depth * 2, gap: 1 }}
            >
              {/* Decorative: the title it sits beside already names the page,
                  so a label here would have a screen reader read it twice. */}
              <ListItemIcon sx={{ minWidth: 32 }}>
                <Icon fontSize="small" />
              </ListItemIcon>
              <ListItemText primary={node.title} />
              {/* §21.5: an over-classified node is pruned with its whole
                  subtree, so every node still here is one this caller may read —
                  the badge says how sensitive it is, not whether it is reachable. */}
              <MarkingLevelBadge level={node.marking.level} levelName={node.marking.levelName} />
              {node.hasRestrictions && (
                <Tooltip title="This page has access restrictions">
                  <LockOutlinedIcon fontSize="small" color="action" aria-label="Has access restrictions" />
                </Tooltip>
              )}
            </ListItemButton>
            {node.children && node.children.length > 0 && (
              <PageTreeList nodes={node.children} spaceKey={spaceKey} depth={depth + 1} />
            )}
          </li>
        )
      })}
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
  const [{ data, fetching, error }] = useSpaceTreeQuery({ variables: { key: spaceKey ?? '' }, pause: !spaceKey })
  const spaceId = data?.space?.id
  const [{ data: treeData }] = useSpacePageTreeQuery({ variables: { spaceId: spaceId ?? '' }, pause: !spaceId })
  const [, watchSpace] = useWatchSpaceMutation()
  const [, unwatchSpace] = useUnwatchSpaceMutation()
  const [{ fetching: creating }, createPage] = useCreatePageMutation()
  const [actionError, setActionError] = useState<string | null>(null)
  const [replicaOrigin, setReplicaOrigin] = useState<string | null>(null)
  const [labelFilter, setLabelFilter] = useState<string | null>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [createError, setCreateError] = useState<string | null>(null)
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
    return <Alert severity="info">{describeLoadFailure('SPACE').summary}</Alert>
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

  const handleCreatePage = async (values: CreatePageValues) => {
    setCreateError(null)
    const result = await createPage({
      input: {
        spaceId: space.id,
        parentPageId: values.parentPageId,
        slug: values.slug,
        title: values.title,
        icon: values.icon,
        content: '',
      },
    })
    // A refusal keeps the dialog open with the typed values — the fix for a
    // duplicate slug or a missing permission is a correction, not a retype.
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
      // Straight into the editor: a page created with empty content exists to be
      // written, and landing on an empty read view would just mean one more click.
      navigate(`/pages/${created.id}/edit`)
    }
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
          {/* No client-side gate, for the same reason as Trash below: there is
              no space-level viewer permission on the wire to gate on (Space
              carries only viewerIsWatching), and inventing one client-side
              would be a guess. Creating a root page needs canEdit on the space,
              which the server enforces; a refusal comes back into the dialog
              inline, with the typed title and slug still there. */}
          <Button
            startIcon={<NoteAddOutlinedIcon />}
            variant="outlined"
            size="small"
            onClick={() => {
              setCreateError(null)
              setCreateOpen(true)
            }}
          >
            New page
          </Button>
          {/* No client-side gate here — the trash query itself is
              permission-filtered server-side, same "let the server decide
              what's visible" approach as everywhere else, rather than
              guessing who should see a Trash link. */}
          <Button component={RouterLink} to={`/spaces/${space.key}/-/trash`} startIcon={<DeleteOutlinedIcon />} variant="outlined" size="small">
            Trash
          </Button>
          {/* Space management lives on its own page now (design.md §6.5.1):
              rename, description, grants, trash and archiving were four
              separate header buttons competing with the page actions. Same
              `grants`-non-empty gate as before — the server returns grant rows
              only to instance/space admins, so an empty list means "not yours
              to manage". */}
          {canManage && (
            <Button
              component={RouterLink}
              to={`/spaces/${space.key}/-/admin`}
              startIcon={<SettingsOutlinedIcon />}
              variant="outlined"
              size="small"
            >
              Space settings
            </Button>
          )}
        </Stack>
      </Stack>

      {/* design.md §12: proactive replica banner — not just the reactive
          dialog after a refused write. */}
      {space.isReplica && (
        <Alert severity="info">
          {replicaBadgeLabel(space.originInstanceId)}. {REPLICA_EXPLANATION}
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
            <Typography color="text.secondary">
              No pages carry this label — clear the filter to see every page.
            </Typography>
          ) : (
            <List dense disablePadding>
              {labelMatches.map((match) => (
                // ListItem (an <li>) wraps the link — a bare <a> as a direct
                // <ul> child is invalid list markup (WCAG 1.3.1 / axe "list").
                <ListItem key={match.id} disablePadding>
                  <ListItemButton component={RouterLink} to={`/pages/${match.id}`}>
                    <ListItemText
                      primary={match.title}
                      secondary={match.path.length > 0 ? match.path.join(' / ') : undefined}
                    />
                  </ListItemButton>
                </ListItem>
              ))}
            </List>
          )}
        </Box>
      ) : (
        <Box role="region" aria-label="Page tree">
          <PageTreeList nodes={tree} spaceKey={space.key} />
        </Box>
      )}

      <CreatePageDialog
        open={createOpen}
        parentLabel={space.name}
        parentOptions={flattenParentOptions(tree)}
        defaultParentId={null}
        error={createError}
        busy={creating}
        onCancel={() => setCreateOpen(false)}
        onConfirm={(values) => void handleCreatePage(values)}
      />

      <ReadOnlyReplicaDialog
        open={replicaOrigin !== null}
        originInstanceId={replicaOrigin}
        onClose={() => setReplicaOrigin(null)}
      />
    </Stack>
  )
}
