import { AvatarGroup, Tooltip } from '@mui/material'
import type { PresenceViewer } from '../realtime/types'
import { UserAvatar } from '../avatars/UserAvatar'

/**
 * design.md §8: "who's here" — avatars only, no attributes, no content.
 *
 * The presence wire payload deliberately carries only userId + displayName
 * + colour (never user attributes), so there is no `hasAvatar` flag to
 * branch on — the viewer's `userId` IS the local User row id the avatar
 * route takes (NotificationsHub sends `user.Id`), so `UserAvatar` fetches
 * on it and a 404 falls back to the initials chip, cached for the session
 * (avatars/avatarCache.ts — never a probe per render). The server-assigned
 * presence colour is passed through so every viewer keeps seeing the same
 * colour for a given user, exactly as before.
 */
export function PresenceAvatars({ viewers }: { viewers: PresenceViewer[] }) {
  if (viewers.length === 0) {
    return null
  }

  return (
    <AvatarGroup max={6} sx={{ '& .MuiAvatar-root': { width: 28, height: 28, fontSize: '0.8rem' } }}>
      {viewers.map((viewer) => (
        <Tooltip key={viewer.userId} title={viewer.displayName}>
          <UserAvatar userId={viewer.userId} displayName={viewer.displayName} colour={viewer.colour} />
        </Tooltip>
      ))}
    </AvatarGroup>
  )
}
