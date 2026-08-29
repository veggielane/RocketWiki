import { useMemo, useState } from 'react'
import {
  Box,
  FormControl,
  IconButton,
  InputLabel,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Link,
  MenuItem,
  Select,
  Skeleton,
  Tooltip,
  Typography,
} from '@mui/material'
import ArticleOutlinedIcon from '@mui/icons-material/ArticleOutlined'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import LockOutlinedIcon from '@mui/icons-material/LockOutlined'
import { Link as RouterLink, useLocation, useNavigate } from 'react-router-dom'
import {
  usePageSpaceRefQuery,
  usePageSubtreeQuery,
  useSpaceListQuery,
  useSpacePageTreeQuery,
} from '../graphql/generated/graphql'
import { describeLoadFailure, describeNoSpaces, replicaBadgeLabel } from '../feedback/unavailableCopy'
import { useIsInstanceAdmin } from '../auth/useIsInstanceAdmin'
import { lookupPageIcon } from '../pages/pageIcons'
import { SYSTEM_SEGMENT } from '../pages/pageSlug'
import { PAGE_TREE_CONTEXT } from '../graphql/treeDependencies'

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
  /**
   * `PageTreeNode.hasChildren` — whether the page has any, independent of how
   * many levels the query selected. The disclosure control is drawn from THIS,
   * not from whether `children` arrived: a document has to stop at some depth,
   * and drawing the chevron from what turned up made the tree assert that a
   * page was a leaf when it was only past the boundary.
   */
  hasChildren?: boolean
  /**
   * `PageTreeNode.hasRestrictions` — the lock badge design.md §6.6 asks for.
   * The space browser's copy of this tree has always drawn it; the rail, which
   * is the tree people actually navigate by, did not, so the one signal saying
   * "this page is restricted" was on the screen nobody visits.
   */
  hasRestrictions?: boolean
  children?: NavNode[]
}

/** Only what the picker draws. Structural, so the generated `SpaceList` row satisfies it. */
interface SpaceChoice {
  key: string
  name: string
  isReplica: boolean
  originInstanceId?: string | null
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

/**
 * The ids of the branches that have to be open on arrival: the current page's
 * ancestors, root-first, AND the page itself.
 *
 * Including the page's own id is what makes "collapsed to your path" mean the
 * path you are on rather than the path up to you — landing somewhere shows what
 * is under it, so the tree answers "where can I go from here" without a click.
 * That is the shape the user picked from, and it is what Confluence does.
 *
 * A page with no children is unaffected: an id in the expanded set that has
 * nothing to reveal draws no chevron and changes nothing.
 *
 * Matches on either address for the same reason the row highlight does — a
 * search result links by id, the ordinary route by slug, and both have to
 * open the same branch.
 */
function ancestorsOfActive(nodes: NavNode[], activePageId?: string, activeSlug?: string): string[] {
  if (activePageId === undefined && activeSlug === undefined) return []
  const found: string[] = []
  function walk(list: NavNode[], trail: string[]): boolean {
    for (const node of list) {
      if (node.id === activePageId || node.slug === activeSlug) {
        found.push(...trail, node.id)
        return true
      }
      if (node.children && walk(node.children, [...trail, node.id])) return true
    }
    return false
  }
  walk(nodes, [])
  return found
}

/**
 * The children of one node, fetched because the tree query did not reach them.
 *
 * Mounted only when a branch is actually opened, so a space costs one small
 * query on arrival plus one per branch someone chooses to look inside — rather
 * than one large payload containing levels nobody expanded.
 *
 * An empty result renders nothing at all: the server prunes what the caller may
 * not see, so "no visible children" and "no children" are the same answer here
 * by design (§6.7). It does not report having found fewer than it expected.
 */
function LazyChildren({
  spaceId,
  spaceKey,
  pageId,
  activePageId,
  activeSlug,
  expanded,
  onToggle,
  depth,
}: {
  spaceId: string
  spaceKey: string
  pageId: string
  activePageId?: string
  activeSlug?: string
  expanded: ReadonlySet<string>
  onToggle: (id: string) => void
  depth: number
}) {
  const [{ data, fetching }] = usePageSubtreeQuery({
    variables: { spaceId, pageId },
    context: PAGE_TREE_CONTEXT,
  })

  if (fetching) {
    return (
      <Box sx={{ pl: 2 + depth * 1.5, py: 0.5 }}>
        <Skeleton variant="text" width="60%" />
      </Box>
    )
  }

  const nodes = (data?.pageSubtree ?? []) as NavNode[]
  if (nodes.length === 0) return null

  return (
    <PageTree
      nodes={nodes}
      spaceId={spaceId}
      spaceKey={spaceKey}
      activePageId={activePageId}
      activeSlug={activeSlug}
      expanded={expanded}
      onToggle={onToggle}
      depth={depth}
    />
  )
}

function PageTree({
  nodes,
  spaceId,
  spaceKey,
  activePageId,
  activeSlug,
  expanded,
  onToggle,
  depth = 0,
}: {
  nodes: NavNode[]
  spaceId: string
  spaceKey: string
  activePageId?: string
  activeSlug?: string
  expanded: ReadonlySet<string>
  onToggle: (id: string) => void
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
        const children = node.children ?? []
        const isExpanded = expanded.has(node.id)
        // What the server says, falling back to what arrived — the fallback is
        // only for a node built before hasChildren existed (a test fixture, an
        // older cached response), never the normal path.
        const hasChildren = node.hasChildren ?? children.length > 0
        return (
          <li key={node.id}>
            {/* The disclosure sits beside the link, never inside it: a button
                nested in an anchor is an axe "nested-interactive" violation,
                and clicking to unfold a branch must not also navigate. */}
            <Box sx={{ display: 'flex', alignItems: 'center', pl: 2 + depth * 1.5 }}>
              {hasChildren ? (
                <IconButton
                  size="small"
                  // Names the branch, not just the verb — several of these sit
                  // in one list, and "Expand" alone would give them all the
                  // same accessible name. The state rides on aria-expanded
                  // rather than on which chevron is drawn (WCAG 1.4.1).
                  aria-label={`${isExpanded ? 'Collapse' : 'Expand'} ${node.title}`}
                  aria-expanded={isExpanded}
                  onClick={() => onToggle(node.id)}
                  // Exactly the spacer's width, and exactly WCAG 2.5.8's floor:
                  // a 16px chevron with `size="small"`'s own padding lands at
                  // 20px, which is a target-size failure the jsdom tier cannot
                  // see (every rect there is 0×0) and the browser tier would.
                  sx={{ p: 0, width: 24, height: 24, flexShrink: 0 }}
                >
                  {isExpanded ? (
                    <ExpandMoreIcon sx={{ fontSize: 16 }} />
                  ) : (
                    <ChevronRightIcon sx={{ fontSize: 16 }} />
                  )}
                </IconButton>
              ) : (
                // A leaf keeps the width but gets no control: a disclosure that
                // opens nothing is worse than none, and without the spacer every
                // leaf title would sit left of its siblings'.
                <Box sx={{ width: 24, flexShrink: 0 }} />
              )}
              <ListItemButton
                component={RouterLink}
                to={`/spaces/${spaceKey}/${node.slug}`}
                // Either address marks the same row. The slug arm is the one that
                // fires on the ordinary route; the id arm keeps /pages/{id}
                // highlighting, which is what a search result or a notification
                // still links to.
                selected={node.id === activePageId || node.slug === activeSlug}
                sx={{ flex: 1, minWidth: 0, py: 0.25 }}
              >
                <ListItemIcon sx={{ minWidth: 28 }}>
                  <Icon sx={{ fontSize: 16 }} />
                </ListItemIcon>
                <ListItemText
                  primary={node.title}
                  slotProps={{ primary: { variant: 'body2', noWrap: true } }}
                />
                {/* §6.6's restriction marker. The tooltip is for the pointer;
                    the `aria-label` is what carries it to everyone else, since
                    a lock glyph alone would be information in an icon. Not a
                    gate — every node here is one the server already decided
                    this caller may see. */}
                {node.hasRestrictions && (
                  <Tooltip title="This page has access restrictions">
                    <LockOutlinedIcon
                      sx={{ fontSize: 14, flexShrink: 0 }}
                      color="action"
                      aria-label="Has access restrictions"
                    />
                  </Tooltip>
                )}
              </ListItemButton>
            </Box>
            {isExpanded &&
              (children.length > 0 ? (
                <PageTree
                  nodes={children}
                  spaceId={spaceId}
                  spaceKey={spaceKey}
                  activePageId={activePageId}
                  activeSlug={activeSlug}
                  expanded={expanded}
                  onToggle={onToggle}
                  depth={depth + 1}
                />
              ) : (
                // Expanded, but the tree query stopped short of these. Fetched by
                // its own component rather than from state held up here, so each
                // branch owns its request: several can be open at once without a
                // queue, and none can overwrite another's result.
                <LazyChildren
                  spaceId={spaceId}
                  spaceKey={spaceKey}
                  pageId={node.id}
                  activePageId={activePageId}
                  activeSlug={activeSlug}
                  expanded={expanded}
                  onToggle={onToggle}
                  depth={depth + 1}
                />
              ))}
          </li>
        )
      })}
    </List>
  )
}

/**
 * The space's name, plus the replica marking when it carries one.
 *
 * Rendered into each option AND, because MUI draws the chosen option's
 * children into the closed field, into the picker itself — which is what keeps
 * design.md §12's proactive marking visible without opening the dropdown. It
 * is one mechanism rather than a badge in the list and a banner underneath,
 * because two of them would drift.
 */
function SpaceOption({ space }: { space: SpaceChoice }) {
  return (
    <Box component="span" sx={{ display: 'inline-flex', alignItems: 'baseline', gap: 1, minWidth: 0 }}>
      <Box component="span" sx={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>
        {space.name}
      </Box>
      {space.isReplica && (
        <Typography component="span" variant="caption" color="text.secondary" noWrap>
          {replicaBadgeLabel(space.originInstanceId)}
        </Typography>
      )}
    </Box>
  )
}

/**
 * The drawer's space picker and the page tree beneath it — server-filtered to
 * spaces the caller can view (design.md §6.7), and a tree that is already
 * permission-filtered and marking-pruned server-side (§21.9), so what appears
 * here is only what this caller may read.
 *
 * The spaces used to be a list, every one of them a row, with the active one
 * expanding to show its tree. They are a dropdown now: one space's hierarchy
 * is what a reader is actually navigating, and a wall of other spaces above it
 * was competing with the thing they came for.
 *
 * The tree arrives whole and opens only along the current page's path.
 * Collapsing is presentation and nothing else — every node here is one the
 * server already decided this caller may see, and a folded branch says
 * "not now", never "not yours".
 */
export function SpaceTreeNav() {
  const { pathname } = useLocation()
  const navigate = useNavigate()
  const { spaceKey: routeSpaceKey, pageId, slug } = routeContext(pathname)
  const [{ data, fetching, error }] = useSpaceListQuery()
  // Only decides which sentence the zero-spaces empty state uses.
  const { isInstanceAdmin } = useIsInstanceAdmin()

  // Only a page route needs this hop; a space route already names its space.
  const [{ data: pageRef }] = usePageSpaceRefQuery({ variables: { id: pageId ?? '' }, pause: !pageId })
  const activeSpaceKey = routeSpaceKey ?? pageRef?.page?.spaceKey ?? undefined
  const activeSpace = data?.spaces.find((s) => s.key === activeSpaceKey)

  // The tree has to be told what changes it (graphql/treeDependencies.ts) —
  // nothing the cache sees on its own connects a created page to this query.
  const [{ data: treeData }] = useSpacePageTreeQuery({
    variables: { spaceId: activeSpace?.id ?? '' },
    pause: !activeSpace,
    context: PAGE_TREE_CONTEXT,
  })

  const tree = useMemo(() => (treeData?.pageTree ?? []) as NavNode[], [treeData])
  const ancestors = useMemo(() => ancestorsOfActive(tree, pageId, slug), [tree, pageId, slug])

  // Which branches are open. Held here rather than derived on every render
  // because it is half route and half user: the route decides what must be
  // open, and a chevron the user pressed has to survive the next keystroke
  // somewhere else in the app.
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set(ancestors))
  // The tree loads after the first render, so the path to the current page
  // arrives late — this is the same "adjust state when a prop changes" pattern
  // the create dialog uses for its parent default, keyed on the path itself so
  // it re-fires on navigation.
  const pathKey = ancestors.join('>')
  const [trackedPathKey, setTrackedPathKey] = useState(pathKey)
  if (trackedPathKey !== pathKey) {
    setTrackedPathKey(pathKey)
    // Union, not replace: arriving somewhere opens the way to it and never
    // folds a branch someone opened by hand. Navigating is not a request to
    // tidy up the tree behind you.
    setExpanded((previous) => new Set([...previous, ...ancestors]))
  }

  const toggle = (id: string) =>
    setExpanded((previous) => {
      const next = new Set(previous)
      if (!next.delete(id)) next.add(id)
      return next
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

  if (data.spaces.length === 0) {
    return (
      <Box sx={{ px: 2, py: 1 }}>
        <Typography variant="caption" color="text.secondary">
          {/* Shared with the space list rather than spelled again here: the rail
              used to tell every reader to "create one to start writing" while
              the button that would is instance-admin-only and hidden from them
              (design.md §6.5.1). */}
          {describeNoSpaces(isInstanceAdmin)}
        </Typography>
      </Box>
    )
  }

  return (
    <>
      <Box sx={{ px: 2, py: 1.5 }}>
        {/* FormControl + Select rather than the `TextField select` used
            elsewhere: this one needs `displayEmpty` and a `renderValue`, and
            only the un-sugared form types the value as the space key it is. */}
        <FormControl fullWidth size="small">
          <InputLabel id="space-picker-label" shrink>
            Space
          </InputLabel>
          <Select
            labelId="space-picker-label"
            id="space-picker"
            label="Space"
            notched
            displayEmpty
            value={activeSpace?.key ?? ''}
            onChange={(e) => navigate(`/spaces/${e.target.value}`)}
            renderValue={(value) => {
              const chosen = data.spaces.find((s) => s.key === value)
              // Off in the admin screens or on /search there is no space to
              // show. An invitation rather than a blank: an empty field next
              // to a label reads as "there are none", which would be a lie.
              if (!chosen) {
                return (
                  <Typography component="span" variant="body2" color="text.secondary">
                    Choose a space
                  </Typography>
                )
              }
              return <SpaceOption space={chosen} />
            }}
          >
            {data.spaces.map((space) => (
              <MenuItem
                key={space.key}
                value={space.key}
                onClick={() => {
                  // Choosing the space you are already in still takes you to
                  // its browse page; `onChange` cannot, because the value did
                  // not change. The old list made every space a link you could
                  // press at any time, and without this there would be no way
                  // back to the space's own page — trash, settings, the label
                  // filter — from a page route.
                  if (space.key === activeSpaceKey) navigate(`/spaces/${space.key}`)
                }}
              >
                <SpaceOption space={space} />
              </MenuItem>
            ))}
          </Select>
        </FormControl>
      </Box>

      {/* Browse: the space's full page list and its label filter.
          /spaces/{key} now lands on the space's default page when it has one,
          so the browser needed an address of its own — and a link to it that is
          not buried in space settings, which is where it lived and where nobody
          would look for "show me this space's pages". Above the tree because it
          is about the same thing the tree is, and outside the <nav> landmark for
          the same reason the picker is: that landmark is the hierarchy itself. */}
      {activeSpace && (
        <Box sx={{ px: 2, pb: 1 }}>
          <Link
            component={RouterLink}
            to={`/spaces/${activeSpace.key}/-/browse`}
            variant="caption"
            underline="hover"
          >
            Browse all pages
          </Link>
        </Box>
      )}

      {/* The picker sits outside the <nav>: it is a form control that happens
          to move you, while this landmark is the page hierarchy itself. The
          list is a <ul> of <li>, each owning its own sub-tree — an <li> outside
          a list element is an axe "list" violation, and it is exactly what a
          first pass at this component shipped. */}
      {activeSpace && (
        <nav aria-label={`Pages in ${activeSpace.name}`}>
          {tree.length > 0 ? (
            <PageTree
              nodes={tree}
              spaceId={activeSpace.id}
              spaceKey={activeSpace.key}
              activePageId={pageId}
              activeSlug={slug}
              expanded={expanded}
              onToggle={toggle}
            />
          ) : (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', pl: 4, py: 0.5 }}>
              No pages yet
            </Typography>
          )}
        </nav>
      )}
    </>
  )
}
