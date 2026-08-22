import { List, ListItemButton, ListItemIcon, ListItemText, Skeleton, Typography, Box } from '@mui/material'
import FolderOutlinedIcon from '@mui/icons-material/FolderOutlined'
import LockOutlinedIcon from '@mui/icons-material/LockOutlined'
import { Link as RouterLink } from 'react-router-dom'
import { useSpaceListQuery } from '../graphql/generated/graphql'

/**
 * Space list in the nav drawer. There is no live API yet (design.md
 * milestone 0/1 — the backend hasn't been scaffolded), so this renders
 * against `schema.placeholder.graphql`'s shape and will simply show its
 * error/empty state until a real `/graphql` endpoint exists.
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
          No spaces loaded yet — the API isn't running in this environment.
        </Typography>
      </Box>
    )
  }

  return (
    <List component="nav" aria-label="Spaces" dense>
      {data.spaces.map((space) => (
        <ListItemButton key={space.key} component={RouterLink} to={`/spaces/${space.key}`}>
          <ListItemIcon>
            {space.isReplica ? <LockOutlinedIcon fontSize="small" /> : <FolderOutlinedIcon fontSize="small" />}
          </ListItemIcon>
          <ListItemText primary={space.name} secondary={space.isReplica ? 'Read-only replica' : undefined} />
        </ListItemButton>
      ))}
    </List>
  )
}
