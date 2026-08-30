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
import RestoreFromTrashOutlinedIcon from '@mui/icons-material/RestoreFromTrashOutlined'
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
import { describeLoadFailure, describeNoPages, REPLICA_EXPLANATION, replicaBadgeLabel } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { filterTreeByLabel } from '../labels/filterTreeByLabel'
import { ReadOnlyReplicaDialog } from '../feedback/ReadOnlyReplicaDialog'
import { CreatePageDialog, type CreatePageValues } from './CreatePageDialog'
import { flattenParentOptions } from './parentOptions'
import { lookupPageIcon } from './pageIcons'
import { MarkingLevelBadge } from '../markings/MarkingLevelBadge'
import { PAGE_TREE_CONTEXT } from '../graphql/treeDependencies'

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

/**
 * Fully expanded, deliberately — this is the one screen whose job is "show me
 * every page in this space", and it is what the label filter above it filters.
 * The rail (app/SpaceTreeNav.tsx) is the opposite: it collapses to your current
 * path because it is for navigating, not surveying. The two trees differing in
 * disclosure is the point; what was NOT the point was this one having no empty
 * state and the rail having no restriction badge, which is why both now do.
 */
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
                  {/* `titleAccess`, not `aria-label` — see SpaceTreeNav's copy of
                      this badge for why an aria-label here reaches nobody. */}
                  <LockOutlinedIcon fontSize="small" color="action" titleAccess="This page has access restrictions" />
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
  // Same declared dependencies as the sidebar's copy — this page creates pages
  // too, and a browser that did not show the one you just made would be the
  // same bug in a second place (graphql/treeDependencies.ts).
  const [{ data: treeData }] = useSpacePageTreeQuery({
    variables: { spaceId: spaceId ?? '' },
    pause: !spaceId,
    context: PAGE_TREE_CONTEXT,
  })
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

  useDocumentTitle(data?.space?.name)

  const tree: PageTreeNode[] = useMemo(() => treeData?.pageTree ?? [], [treeData])
  const availableLabels = useMemo(() => distinctLabels(tree), [tree])
  const labelMatches = useMemo(() => (labelFilter ? filterTreeByLabel(tree, labelFilter) : []), [tree, labelFilter])

    // FIRST LOAD ONLY. urql retains `data` across a refetch and flips `fetching`
  // true (urql.js computeNextState), so a bare `if (fetching)` threw the screen
  // away on every post-write refetch: content, scroll position and keyboard
  // focus all went with it. `&& !data` keeps the rendered screen up while the
  // re-read happens underneath it.
  if (fetching && !data) {
    // Title skeleton as well as body: this screen has an h1, so a bare
    // rectangle under-describes the layout and the heading pops in late.
    return (
      <Stack spacing={1}>
        <Skeleton variant="text" width="40%" height={48} />
        <Skeleton variant="rectangular" height={300} />
      </Stack>
    )
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
      <PageHeader
        title={space.name}
        actions={
          <>
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
                guessing who should see a Trash link.

                `RestoreFromTrashOutlined`, not `DeleteOutlined`: the plain
                trash can is the DESTRUCTIVE verb everywhere else in the app
                (the page's Delete action, every remove-row button), and using
                it for a navigation link asked people to press "delete" to go
                and look at something. */}
            <Button
              component={RouterLink}
              to={`/spaces/${space.key}/-/trash`}
              startIcon={<RestoreFromTrashOutlinedIcon />}
              variant="outlined"
              size="small"
            >
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
            {/* The one emphasised action on this screen, matching the space
                list's "New space". It used to be `outlined` like its three
                neighbours, so the screen had four equal buttons and no call to
                action.

                No client-side gate: there is no space-level viewer permission on
                the wire to gate on (Space carries only viewerIsWatching), and
                inventing one client-side would be a guess. Creating a root page
                needs canEdit on the space, which the server enforces; a refusal
                comes back into the dialog inline, with the typed title and slug
                still there. */}
            <Button
              startIcon={<NoteAddOutlinedIcon />}
              variant="contained"
              size="small"
              onClick={() => {
                setCreateError(null)
                setCreateOpen(true)
              }}
            >
              New page
            </Button>
          </>
        }
      />

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
          {tree.length > 0 ? (
            <PageTreeList nodes={tree} spaceKey={space.key} />
          ) : (
            /* A space with no pages rendered an empty <List> and nothing else —
               and this is the screen you land on right after creating one.
               web/README.md's rule: the fact AND the consequence. */
            <Typography color="text.secondary">{describeNoPages(true)}</Typography>
          )}
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
