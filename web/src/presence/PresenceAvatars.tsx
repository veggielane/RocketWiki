import { AvatarGroup, Tooltip } from '@mui/material'
import type { PresenceViewer } from '../realtime/types'
import { UserAvatar } from '../avatars/UserAvatar'

/**
 * design.md §8: "who's here" — avatars only, no attributes, no content.
 *
 * The presence wire payload carries userId + displayName + colour + hasAvatar,
 * and nothing else — never user attributes. The viewer's `userId` IS the local
 * User row id the avatar route takes (NotificationsHub sends `user.Id`), and
 * `hasAvatar` is what lets this render initials for a viewer with no uploaded
 * avatar instead of probing `GET /users/{id}/avatar` and taking a 404 for them
 * on every page view. The server-assigned presence colour is passed through so
 * every viewer keeps seeing the same colour for a given user.
 */
export function PresenceAvatars({ viewers }: { viewers: PresenceViewer[] }) {
  if (viewers.length === 0) {
    return null
  }

  return (
    <AvatarGroup max={6} sx={{ '& .MuiAvatar-root': { width: 28, height: 28, fontSize: '0.8rem' } }}>
      {viewers.map((viewer) => (
        <Tooltip key={viewer.userId} title={viewer.displayName}>
          <UserAvatar
            userId={viewer.userId}
            hasAvatar={viewer.hasAvatar}
            displayName={viewer.displayName}
            colour={viewer.colour}
          />
        </Tooltip>
      ))}
    </AvatarGroup>
  )
}
