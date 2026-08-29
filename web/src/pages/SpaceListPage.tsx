import { Alert, Button, Card, CardActionArea, CardContent, Chip, Skeleton, Stack, Typography } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import AddIcon from '@mui/icons-material/Add'
import CloudSyncOutlinedIcon from '@mui/icons-material/CloudSyncOutlined'
import { useSpaceListQuery } from '../graphql/generated/graphql'
import { useIsInstanceAdmin } from '../auth/useIsInstanceAdmin'
import { describeLoadFailure, describeNoSpaces, replicaBadgeLabel } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'

/**
 * Space directory. Replica spaces (design.md §12) carry a proactive
 * "Replica of {origin}" chip via `Space.isReplica`/`originInstanceId`.
 */
export function SpaceListPage() {
  const [{ data, fetching, error }] = useSpaceListQuery()
  const { isInstanceAdmin } = useIsInstanceAdmin()
  useDocumentTitle('Spaces')

  return (
    <Stack spacing={2}>
      <PageHeader
        title="Spaces"
        actions={
          <>
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
          </>
        }
      />

      {/* Title skeleton as well as body, matching the page/details/settings
          screens — a bare rectangle under a heading that has not rendered yet
          under-describes the layout and makes the h1 pop in. */}
      {fetching && (
        <Stack spacing={1}>
          <Skeleton variant="text" width="40%" height={48} />
          <Skeleton variant="rectangular" height={120} />
        </Stack>
      )}

      {(error || (!fetching && !data)) && <Alert severity="info">{describeLoadFailure('SPACE_LIST').summary}</Alert>}

      {/* True empty (loaded, zero spaces) — distinct from the load failure
          above. The consequence differs by who's looking: only instance
          admins can create a space (design.md §6.5.1), and the sentence for
          each is shared with the rail's tree so the two cannot drift. */}
      {data?.spaces.length === 0 && <Typography color="text.secondary">{describeNoSpaces(isInstanceAdmin)}</Typography>}

      {data?.spaces.map((space) => (
        <Card key={space.key} variant="outlined">
          <CardActionArea component={RouterLink} to={`/spaces/${space.key}`}>
            <CardContent>
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
                <Typography variant="h6" component="h2">
                  {space.name}
                </Typography>
                {space.isReplica && (
                  <Chip
                    size="small"
                    icon={<CloudSyncOutlinedIcon />}
                    label={replicaBadgeLabel(space.originInstanceId)}
                  />
                )}
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
