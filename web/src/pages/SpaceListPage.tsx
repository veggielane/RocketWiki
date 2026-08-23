import { Alert, Button, Card, CardActionArea, CardContent, Skeleton, Stack, Typography } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import AddIcon from '@mui/icons-material/Add'
import { useSpaceListQuery } from '../graphql/generated/graphql'
import { useIsInstanceAdmin } from '../auth/useIsInstanceAdmin'

/**
 * NOTE (schema reconciliation): the replica chip is gone — see
 * SpaceTreeNav.tsx (the real Space exposes no client-usable isReplica).
 */
export function SpaceListPage() {
  const [{ data, fetching, error }] = useSpaceListQuery()
  const { isInstanceAdmin } = useIsInstanceAdmin()

  return (
    <Stack spacing={2}>
      <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="h4" component="h1">
          Spaces
        </Typography>
        <Stack direction="row" spacing={1}>
          <Button component={RouterLink} to="/spaces/archived" size="small">
            Archived spaces
          </Button>
          {/* Creating a space has no space-admin to check yet, so this is
              instance-admin-only (design.md §6.5.1) — hidden rather than
              disabled for the same reason as everywhere else. */}
          {isInstanceAdmin && (
            <Button component={RouterLink} to="/spaces/new" startIcon={<AddIcon />} variant="contained" size="small">
              New space
            </Button>
          )}
        </Stack>
      </Stack>

      {fetching && <Skeleton variant="rectangular" height={120} />}

      {(error || (!fetching && !data)) && <Alert severity="info">No spaces loaded.</Alert>}

      {data?.spaces.map((space) => (
        <Card key={space.key} variant="outlined">
          <CardActionArea component={RouterLink} to={`/spaces/${space.key}`}>
            <CardContent>
              <Typography variant="h6">{space.name}</Typography>
              {space.description && (
                <Typography variant="body2" color="text.secondary">
                  {space.description}
                </Typography>
              )}
            </CardContent>
          </CardActionArea>
        </Card>
      ))}
    </Stack>
  )
}
