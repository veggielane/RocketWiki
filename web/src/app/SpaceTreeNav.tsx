import { List, ListItemButton, ListItemIcon, ListItemText, Skeleton, Typography, Box } from '@mui/material'
import FolderOutlinedIcon from '@mui/icons-material/FolderOutlined'
import { Link as RouterLink } from 'react-router-dom'
import { useSpaceListQuery } from '../graphql/generated/graphql'

/**
 * Space list in the nav drawer — server-filtered to spaces the caller can
 * view (design.md §6.7).
 *
 * NOTE (schema reconciliation): the placeholder's `isReplica` flag is gone —
 * the real Space only offers `isReplicaOf(localInstanceId!)`, an argument
 * the browser can't supply (reported contract gap), so replica spaces are
 * not marked here; replica-ness still surfaces on any write via the typed
 * ReadOnlyReplica error (design.md §12).
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
          No spaces loaded.
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
          <ListItemText primary={space.name} />
        </ListItemButton>
      ))}
    </List>
  )
}
