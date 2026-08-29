import { Breadcrumbs, Link, Typography } from '@mui/material'
import NavigateNextIcon from '@mui/icons-material/NavigateNext'
import { Link as RouterLink, useLocation } from 'react-router-dom'
import { usePageSpaceRefQuery } from '../graphql/generated/graphql'
import { SYSTEM_SEGMENT } from '../pages/pageSlug'

interface Crumb {
  label: string
  /** Absent on the last crumb, which is where you already are. */
  to?: string
}

const SPACES: Crumb = { label: 'Spaces', to: '/' }
const ADMIN: Crumb = { label: 'Admin', to: '/admin' }

/** The system pages a space owns, keyed by the segment after `/-/`. */
const SPACE_SYSTEM_PAGES: Record<string, string> = {
  admin: 'Space settings',
  grants: 'Permissions',
  trash: 'Trash',
  'import-report': 'Import report',
}

const PAGE_SUBPAGES: Record<string, string> = {
  edit: 'Editing',
  properties: 'Properties',
  permissions: 'Permissions',
}

const ADMIN_PAGES: Record<string, string> = {
  audit: 'Audit log',
  sync: 'Sync status',
  emojis: 'Emoji',
  'property-keys': 'Property keys',
}

/**
 * Crumbs for everything addressed by its own path.
 *
 * A page route stops at its space rather than naming the page. The shell knows
 * a page's slug, not its title, and de-slugifying one would put a *guess* at a
 * page's name in the chrome — `stage-two-ignition-anomaly` is not
 * "Stage two ignition anomaly review". The page's own heading says the title
 * a line below, so the crumb spends itself on the thing the heading does not
 * repeat: which space you are in.
 */
function crumbsFor(pathname: string, pageSpaceKey?: string): Crumb[] {
  const [first, ...rest] = pathname.split('/').filter(Boolean)

  if (first === undefined) return [{ label: 'Spaces' }]

  if (first === 'spaces') {
    const [key, second, third] = rest
    if (key === undefined) return [{ label: 'Spaces' }]
    if (key === 'new') return [SPACES, { label: 'New space' }]
    if (key === 'archived') return [SPACES, { label: 'Archived spaces' }]
    const spaceKey = decodeURIComponent(key)
    const space: Crumb = { label: spaceKey, to: `/spaces/${key}` }
    if (second === SYSTEM_SEGMENT && third !== undefined) {
      return [SPACES, space, { label: SPACE_SYSTEM_PAGES[third] ?? third }]
    }
    // `/spaces/{key}/{slug}` — a page, so the space is where we stop.
    return [SPACES, { label: spaceKey }]
  }

  if (first === 'pages') {
    const space: Crumb | undefined =
      pageSpaceKey === undefined ? undefined : { label: pageSpaceKey, to: `/spaces/${pageSpaceKey}` }
    const sub = rest[1] === undefined ? undefined : PAGE_SUBPAGES[rest[1]]
    if (sub === undefined) {
      return space === undefined ? [{ label: 'Spaces' }] : [SPACES, { label: space.label }]
    }
    return space === undefined ? [SPACES, { label: sub }] : [SPACES, space, { label: sub }]
  }

  if (first === 'admin') {
    const section = rest[0]
    if (section === undefined) return [{ label: 'Admin' }]
    return [ADMIN, { label: ADMIN_PAGES[section] ?? section }]
  }

  const top: Record<string, string> = {
    search: 'Search',
    ask: 'Ask the wiki',
    settings: 'Settings',
  }
  return [{ label: top[first] ?? first }]
}

/**
 * The template's header breadcrumb: muted ancestors, a chevron separator in
 * `action.disabled`, and the current location in bold `text.primary`.
 */
export function AppBreadcrumbs() {
  const { pathname } = useLocation()
  // Only a `/pages/{id}` route needs this hop, and SpaceTreeNav asks the same
  // question for the same route — urql serves the second caller from cache
  // rather than issuing a request.
  const pageId = /^\/pages\/([^/]+)/.exec(pathname)?.[1]
  const [{ data }] = usePageSpaceRefQuery({ variables: { id: pageId ?? '' }, pause: !pageId })

  const crumbs = crumbsFor(pathname, data?.page?.spaceKey ?? undefined)

  return (
    <Breadcrumbs
      aria-label="breadcrumb"
      separator={<NavigateNextIcon fontSize="small" />}
      sx={{
        minWidth: 0,
        '& .MuiBreadcrumbs-separator': { color: 'action.disabled', mx: 0.5 },
        '& .MuiBreadcrumbs-ol': { alignItems: 'center', flexWrap: 'nowrap' },
        '& .MuiBreadcrumbs-li': { minWidth: 0 },
      }}
    >
      {crumbs.map((crumb, index) =>
        crumb.to === undefined || index === crumbs.length - 1 ? (
          <Typography key={crumb.label} noWrap sx={{ color: 'text.primary', fontWeight: 600 }}>
            {crumb.label}
          </Typography>
        ) : (
          <Link
            key={crumb.label}
            component={RouterLink}
            to={crumb.to}
            underline="hover"
            noWrap
            sx={{ color: 'text.secondary' }}
          >
            {crumb.label}
          </Link>
        ),
      )}
    </Breadcrumbs>
  )
}
