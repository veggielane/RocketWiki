import { Alert, Button, Card, CardActionArea, CardContent, Chip, Skeleton, Stack, Typography } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import AddIcon from '@mui/icons-material/Add'
import { useCurrentUserQuery, useSpaceListQuery } from '../graphql/generated/graphql'

export function SpaceListPage() {
  const [{ data, fetching, error }] = useSpaceListQuery()
  const [{ data: meData }] = useCurrentUserQuery()

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
          {meData?.me.isInstanceAdmin && (
            <Button component={RouterLink} to="/spaces/new" startIcon={<AddIcon />} variant="contained" size="small">
              New space
            </Button>
          )}
        </Stack>
      </Stack>

      {fetching && <Skeleton variant="rectangular" height={120} />}

      {(error || (!fetching && !data)) && (
        <Alert severity="info">No spaces loaded yet — there's no live API in this environment.</Alert>
      )}

      {data?.spaces.map((space) => (
        <Card key={space.key} variant="outlined">
          <CardActionArea component={RouterLink} to={`/spaces/${space.key}`}>
            <CardContent>
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <Typography variant="h6">{space.name}</Typography>
                {space.isReplica && <Chip label="Read-only replica" size="small" color="default" />}
              </Stack>
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
