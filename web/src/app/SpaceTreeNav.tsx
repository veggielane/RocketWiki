import { List, ListItemButton, ListItemIcon, ListItemText, Skeleton, Typography, Box } from '@mui/material'
import FolderOutlinedIcon from '@mui/icons-material/FolderOutlined'
import { Link as RouterLink } from 'react-router-dom'
import { useSpaceListQuery } from '../graphql/generated/graphql'
import { describeLoadFailure, replicaBadgeLabel } from '../feedback/unavailableCopy'

/**
 * Space list in the nav drawer — server-filtered to spaces the caller can
 * view (design.md §6.7). Replica spaces are marked proactively via
 * `Space.isReplica` (design.md §12), not just reactively on a refused
 * write.
 */
export function SpaceTreeNav() {
  const [{ data, fetching, error }] = useSpaceListQuery()

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

  return (
    <List component="nav" aria-label="Spaces" dense>
      {data.spaces.map((space) => (
        <ListItemButton key={space.key} component={RouterLink} to={`/spaces/${space.key}`}>
          <ListItemIcon>
            <FolderOutlinedIcon fontSize="small" />
          </ListItemIcon>
          <ListItemText
            primary={space.name}
            secondary={space.isReplica ? replicaBadgeLabel(space.originInstanceId) : undefined}
          />
        </ListItemButton>
      ))}
    </List>
  )
}
