import { List, ListItemButton, ListItemIcon, ListItemText, Skeleton, Typography, Box } from '@mui/material'
import FolderOutlinedIcon from '@mui/icons-material/FolderOutlined'
import ArticleOutlinedIcon from '@mui/icons-material/ArticleOutlined'
import { Link as RouterLink, useLocation } from 'react-router-dom'
import {
  usePageSpaceRefQuery,
  useSpaceListQuery,
  useSpacePageTreeQuery,
} from '../graphql/generated/graphql'
import { describeLoadFailure, replicaBadgeLabel } from '../feedback/unavailableCopy'

/** One node of the nav tree. Only what the drawer renders — no labels, no markings. */
interface NavNode {
  id: string
  title: string
  children?: NavNode[]
}

/**
 * Which space the current route is "in", read from the URL.
 *
 * A page route only carries a page id, so the space has to be resolved from it;
 * a space route carries the key directly. Parsed from the pathname rather than
 * `useParams` on purpose: this component renders in the layout, above the route
 * that matched, so its own params are empty — the child's are not in scope.
 */
function routeContext(pathname: string): { spaceKey?: string; pageId?: string } {
  const space = /^\/spaces\/([^/]+)/.exec(pathname)
  if (space) return { spaceKey: decodeURIComponent(space[1]!) }
  const page = /^\/pages\/([^/]+)/.exec(pathname)
  if (page) return { pageId: page[1]! }
  return {}
}

function PageTree({ nodes, activePageId, depth = 0 }: { nodes: NavNode[]; activePageId?: string; depth?: number }) {
  // A plain <ul>: the <li> children below need a list parent, and nesting a
  // sub-tree inside its parent's <li> is what makes the hierarchy real to a
  // screen reader rather than a flat run of links with decorative indentation.
  return (
    <List dense disablePadding>
      {nodes.map((node) => (
        <li key={node.id}>
          <ListItemButton
            component={RouterLink}
            to={`/pages/${node.id}`}
            selected={node.id === activePageId}
            sx={{ pl: 4 + depth * 2, py: 0.25 }}
          >
            <ListItemIcon sx={{ minWidth: 28 }}>
              <ArticleOutlinedIcon sx={{ fontSize: 16 }} />
            </ListItemIcon>
            <ListItemText
              primary={node.title}
              slotProps={{ primary: { variant: 'body2', noWrap: true } }}
            />
          </ListItemButton>
          {node.children && node.children.length > 0 && (
            <PageTree nodes={node.children} activePageId={activePageId} depth={depth + 1} />
          )}
        </li>
      ))}
    </List>
  )
}

/**
 * Space list in the nav drawer — server-filtered to spaces the caller can
 * view (design.md §6.7). Replica spaces are marked proactively via
 * `Space.isReplica` (design.md §12), not just reactively on a refused
 * write.
 *
 * The space you are currently in expands to show its page tree, so the
 * hierarchy is navigable from anywhere in that space rather than only from the
 * space's own browse page. Only the active space expands: every space's tree at
 * once would be a query per space and a wall of links, and the tree is
 * permission-filtered and marking-pruned server-side (§21.9), so what appears
 * here is already only what this caller may read.
 */
export function SpaceTreeNav() {
  const { pathname } = useLocation()
  const { spaceKey: routeSpaceKey, pageId } = routeContext(pathname)
  const [{ data, fetching, error }] = useSpaceListQuery()

  // Only a page route needs this hop; a space route already names its space.
  const [{ data: pageRef }] = usePageSpaceRefQuery({ variables: { id: pageId ?? '' }, pause: !pageId })
  const activeSpaceKey = routeSpaceKey ?? pageRef?.page?.spaceKey ?? undefined
  const activeSpace = data?.spaces.find((s) => s.key === activeSpaceKey)

  const [{ data: treeData }] = useSpacePageTreeQuery({
    variables: { spaceId: activeSpace?.id ?? '' },
    pause: !activeSpace,
  })

  if (fetching) {
    return (
      <Box sx={{ px: 2, py: 1 }}>
        <Skeleton variant="text" width="80%" />
        <Skeleton variant="text" width="60%" />
        <Skeleton variant="text" width="70%" />
      </Box>
    )
  }

  if (error || !data) {
    return (
      <Box sx={{ px: 2, py: 1 }}>
        <Typography variant="caption" color="text.secondary">
          {describeLoadFailure('SPACE_LIST').summary}
        </Typography>
      </Box>
    )
  }

  const tree = (treeData?.pageTree ?? []) as NavNode[]

  // <nav> wrapping a <ul>, not `component="nav"` on the list itself: the items
  // are <li> (each one owning its space's sub-tree), and an <li> outside a list
  // element is an axe "list" violation.
  return (
    <nav aria-label="Spaces">
      <List dense>
        {data.spaces.map((space) => {
          const isActive = space.key === activeSpaceKey
          return (
            <li key={space.key}>
              <ListItemButton
                component={RouterLink}
                to={`/spaces/${space.key}`}
                selected={isActive && pathname === `/spaces/${space.key}`}
              >
                <ListItemIcon>
                  <FolderOutlinedIcon fontSize="small" />
                </ListItemIcon>
                <ListItemText
                  primary={space.name}
                  secondary={space.isReplica ? replicaBadgeLabel(space.originInstanceId) : undefined}
                />
              </ListItemButton>
              {isActive && tree.length > 0 && <PageTree nodes={tree} activePageId={pageId} />}
              {isActive && tree.length === 0 && (
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', pl: 4, py: 0.5 }}>
                  No pages yet
                </Typography>
              )}
            </li>
          )
        })}
      </List>
    </nav>
  )
}
