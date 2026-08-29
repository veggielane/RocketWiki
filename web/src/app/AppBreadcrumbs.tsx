import { Breadcrumbs, Link, Typography } from '@mui/material'
import NavigateNextIcon from '@mui/icons-material/NavigateNext'
import { Link as RouterLink, useLocation } from 'react-router-dom'
import { usePageSpaceRefQuery } from '../graphql/generated/graphql'
import { crumbsFor } from './routeCrumbs'

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
  // Only a `/pages/{id}` route needs this hop, and the shell asks the same
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
