import { AvatarGroup, Avatar, Tooltip } from '@mui/material'
import type { PresenceViewer } from '../realtime/types'

/** design.md §8: "who's here" — avatars only, no attributes, no content. */
export function PresenceAvatars({ viewers }: { viewers: PresenceViewer[] }) {
  if (viewers.length === 0) {
    return null
  }

  return (
    <AvatarGroup max={6} sx={{ '& .MuiAvatar-root': { width: 28, height: 28, fontSize: '0.8rem' } }}>
      {viewers.map((viewer) => (
        <Tooltip key={viewer.connectionId} title={viewer.displayName}>
          <Avatar sx={{ bgcolor: viewer.colour }}>{viewer.displayName.slice(0, 1).toUpperCase()}</Avatar>
        </Tooltip>
      ))}
    </AvatarGroup>
  )
}
