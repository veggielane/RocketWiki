import { Link, type LinkProps } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import { UserAvatar } from '../avatars/UserAvatar'
import { profilePath } from './profilePath'

export interface UserLinkProps {
  /** A `UserRef`, or anything shaped like one: the local id the profile route takes, the name to show, the avatar flag. */
  user: { id: string; displayName: string; hasAvatar?: boolean }
  /**
   * Face size in px, beside the name. Omit for a name-only link. The face is
   * decorative here — the name is the link's text — so it is hidden from
   * assistive tech rather than announced a second time.
   */
  avatarSize?: number
  /** Typography variant for the name. Defaults to the surrounding text's. */
  variant?: LinkProps['variant']
  sx?: LinkProps['sx']
}

/**
 * The one way a person is named on a screen: display name (with optional
 * face) as a link to their profile. Comments, attachments, history and the
 * space owner all render a `UserRef`, and each used to render it as plain
 * text in its own way — so a profile page would have had four bylines to
 * teach, one at a time, that a name can be followed.
 *
 * MUI's Link with its default underline, not a bare RouterLink: a name in a
 * line of prose ("2 KB · uploaded by Grace") that differed from its
 * neighbours by colour alone is WCAG 1.4.1's link-in-text-block failure,
 * which is exactly why the theme did not adopt the template's colour-only
 * link style (theme/componentCustomizations.ts).
 */
export function UserLink({ user, avatarSize, variant = 'inherit', sx }: UserLinkProps) {
  // MUI's documented composition idiom for a passthrough `sx` (it can be an
  // array or function, so object-spreading it would be wrong).
  const outerSx = Array.isArray(sx) ? sx : [sx]
  return (
    <Link
      component={RouterLink}
      to={profilePath(user.id)}
      variant={variant}
      sx={[{ display: 'inline-flex', alignItems: 'center', gap: 0.75, maxWidth: '100%' }, ...outerSx]}
    >
      {avatarSize !== undefined && (
        <UserAvatar
          userId={user.id}
          hasAvatar={user.hasAvatar ?? false}
          displayName={user.displayName}
          size={avatarSize}
          aria-hidden
        />
      )}
      {user.displayName}
    </Link>
  )
}
