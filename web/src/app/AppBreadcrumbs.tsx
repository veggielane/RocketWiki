import { Breadcrumbs, Link, Typography } from '@mui/material'
import NavigateNextIcon from '@mui/icons-material/NavigateNext'
import { Link as RouterLink, useLocation } from 'react-router-dom'
import { crumbsFor } from './routeCrumbs'
import { useCanonicalSpace } from './useCanonicalSpaceKey'

/**
 * The template's header breadcrumb: muted ancestors, a chevron separator in
 * `action.disabled`, and the current location in bold `text.primary`.
 *
 * The route-to-label maps live in routeCrumbs.ts rather than here because the
 * browser tab title is derived from the same walk (app/documentTitle.ts) — two
 * copies drifted from the router once already, which is how `/pages/{id}/details`
 * ended up with no crumb at all and the space browser rendered a raw "browse".
 */
export function AppBreadcrumbs() {
  const { pathname } = useLocation()
  // The space's NAME, as the server spells it — not the key, and not the
  // casing that happened to be typed into the URL. Both underlying queries are
  // ones the shell and rail already run, so urql answers from cache.
  const space = useCanonicalSpace(pathname)
  const crumbs = crumbsFor(pathname, space)

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
      {/* A crumb is a link whenever it HAS a destination — including the last
          one. The old rule ("last crumb is always plain text") assumed the last
          crumb is always where you are, which is false on a page route: there
          the final crumb names the SPACE while you are looking at a page, so
          rendering it as bold "you are here" was both a dead end and a lie
          about your location. */}
      {crumbs.map((crumb) =>
        crumb.to === undefined ? (
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
