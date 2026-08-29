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
import { lookupPageIcon } from '../pages/pageIcons'
import { SYSTEM_SEGMENT } from '../pages/pageSlug'

/** One node of the nav tree. Only what the drawer renders — no labels, no markings. */
interface NavNode {
  id: string
  title: string
  /**
   * `PageTreeNode.icon`. Typed as a plain string, not the generated `PageIcon`
   * union: the tree renders whatever the server sent, and a name from a newer
   * icon set degrades to the generic glyph rather than being a type error here
   * (see pages/pageIcons.tsx).
   */
  icon?: string | null
  slug: string
  children?: NavNode[]
}

/**
 * Which space the current route is "in", and which page within it, read from
 * the URL.
 *
 * Both ways of addressing a page turn up here. `/spaces/{key}/{slug}` names the
 * space and the page together, which is the ordinary case; `/pages/{id}` names
 * only the page, so the space has to be resolved from it. The slug is enough to
 * find the node — slugs are unique per space and every tree node carries one —
 * so the common route needs no extra query to know what to highlight.
 *
 * The system pages are the exception: `/spaces/{key}/-/admin` and friends sit
 * under the reserved `-` segment, and none of them is a page, so the slug arm
 * deliberately ignores it rather than hunting for a node that cannot exist.
 *
 * Parsed from the pathname rather than `useParams` on purpose: this component
 * renders in the layout, above the route that matched, so its own params are
 * empty — the child's are not in scope.
 */
function routeContext(pathname: string): { spaceKey?: string; pageId?: string; slug?: string } {
  const space = /^\/spaces\/([^/]+)(?:\/([^/]+))?/.exec(pathname)
  if (space) {
    const slug = space[2] ? decodeURIComponent(space[2]) : undefined
    return {
      spaceKey: decodeURIComponent(space[1]!),
      slug: slug === SYSTEM_SEGMENT ? undefined : slug,
    }
  }
  const page = /^\/pages\/([^/]+)/.exec(pathname)
  if (page) return { pageId: page[1]! }
  return {}
}

function PageTree({
  nodes,
  spaceKey,
  activePageId,
  activeSlug,
  depth = 0,
}: {
  nodes: NavNode[]
  spaceKey: string
  activePageId?: string
  activeSlug?: string
  depth?: number
}) {
  // A plain <ul>: the <li> children below need a list parent, and nesting a
  // sub-tree inside its parent's <li> is what makes the hierarchy real to a
  // screen reader rather than a flat run of links with decorative indentation.
  return (
    <List dense disablePadding>
      {nodes.map((node) => {
        // The page's own icon when it has one, the generic page glyph when it
        // doesn't — and when it names one this build has no glyph for. The
        // glyph is decorative: the title beside it already names the page.
        const Icon = lookupPageIcon(node.icon)?.Icon ?? ArticleOutlinedIcon
        return (
          <li key={node.id}>
            <ListItemButton
              component={RouterLink}
              to={`/spaces/${spaceKey}/${node.slug}`}
              // Either address marks the same row. The slug arm is the one that
              // fires on the ordinary route; the id arm keeps /pages/{id}
              // highlighting, which is what a search result or a notification
              // still links to.
              selected={node.id === activePageId || node.slug === activeSlug}
              sx={{ pl: 4 + depth * 2, py: 0.25 }}
            >
              <ListItemIcon sx={{ minWidth: 28 }}>
                <Icon sx={{ fontSize: 16 }} />
              </ListItemIcon>
              <ListItemText
                primary={node.title}
                slotProps={{ primary: { variant: 'body2', noWrap: true } }}
              />
            </ListItemButton>
            {node.children && node.children.length > 0 && (
              <PageTree
                nodes={node.children}
                spaceKey={spaceKey}
                activePageId={activePageId}
                activeSlug={activeSlug}
                depth={depth + 1}
              />
            )}
          </li>
        )
      })}
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
  const { spaceKey: routeSpaceKey, pageId, slug } = routeContext(pathname)
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
              {isActive && tree.length > 0 && (
                <PageTree nodes={tree} spaceKey={space.key} activePageId={pageId} activeSlug={slug} />
              )}
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
