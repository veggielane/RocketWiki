import { forwardRef, useEffect, useState } from 'react'
import { Avatar, type AvatarProps } from '@mui/material'
import { colourForUser } from '../presence/colourForUser'
import { getAvatarUrl, peekAvatarUrl } from './avatarCache'

export interface UserAvatarProps extends Omit<AvatarProps, 'src' | 'children'> {
  /** Local User row id (`UserRef.id` / `me.localUserId`) — the id `GET /users/{id}/avatar` takes. */
  userId: string | null | undefined
  /**
   * `UserRef.hasAvatar` / `me.hasAvatar` where the GraphQL read carries it:
   * false renders initials without ever probing the route; true fetches.
   * Omit it only where the wire payload genuinely lacks the flag (presence)
   * — then we fetch on the id and a 404 falls back to initials, cached.
   */
  hasAvatar?: boolean
  displayName: string
  /** Width/height in px (MUI's Avatar default is 40). */
  size?: number
  /** Overrides the derived initials colour — presence passes its server-assigned colour so every viewer sees the same one. */
  colour?: string
}

/**
 * The one identity-face component (design.md §19): image when the user has
 * an uploaded avatar, initials + `colourForUser` otherwise — exactly the
 * fallback the app rendered before avatars existed. Images come through
 * the authenticated route via the session blob-URL cache (avatarCache.ts),
 * never a direct `<img src>` to the API (§10's inline-image rule).
 *
 * forwardRef because MUI's Tooltip/AvatarGroup attach to their child via ref.
 */
export const UserAvatar = forwardRef<HTMLDivElement, UserAvatarProps>(function UserAvatar(
  { userId, hasAvatar, displayName, size, colour, sx, ...rest },
  ref,
) {
  const shouldFetch = Boolean(userId) && hasAvatar !== false
  const initialUrl = () => (shouldFetch && userId ? (peekAvatarUrl(userId) ?? null) : null)
  const [url, setUrl] = useState<string | null>(initialUrl)
  // Render-time reset when the identity/flag props change (React's
  // documented "adjusting state when a prop changes" pattern, same as
  // useAttachmentBlobUrl) — never a setState inside the effect body.
  const [tracked, setTracked] = useState({ userId, shouldFetch })
  if (tracked.userId !== userId || tracked.shouldFetch !== shouldFetch) {
    setTracked({ userId, shouldFetch })
    setUrl(initialUrl())
  }

  useEffect(() => {
    if (!shouldFetch || !userId) {
      return
    }
    let cancelled = false
    // The cache resolves null for "no avatar" — initials stay up. No URL
    // revocation here: the session cache owns the object URLs (they're
    // shared across every mount of the same user), and revokes on eviction.
    void getAvatarUrl(userId).then((resolved) => {
      if (!cancelled) setUrl(resolved)
    })
    return () => {
      cancelled = true
    }
  }, [shouldFetch, userId])

  const initialsColour = colour ?? colourForUser(userId ?? displayName)
  const sizeSx = size !== undefined ? { width: size, height: size, fontSize: `${Math.max(size * 0.45, 10)}px` } : {}
  // MUI's documented composition idiom for a passthrough `sx` (it can be an
  // array or function, so object-spreading it would be wrong).
  const outerSx = Array.isArray(sx) ? sx : [sx]

  if (url) {
    return <Avatar ref={ref} src={url} alt={displayName} sx={[sizeSx, ...outerSx]} {...rest} />
  }
  return (
    <Avatar ref={ref} alt={displayName} sx={[{ bgcolor: initialsColour }, sizeSx, ...outerSx]} {...rest}>
      {(displayName || '?').slice(0, 1).toUpperCase()}
    </Avatar>
  )
})
