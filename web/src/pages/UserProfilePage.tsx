import { useId } from 'react'
import { useParams } from 'react-router-dom'
import { Alert, List, ListItem, ListItemText, Paper, Skeleton, Stack, Typography } from '@mui/material'
import { useUserProfileQuery } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { UserAvatar } from '../avatars/UserAvatar'
import {
  EXTERNAL_ACCOUNT_NOTE,
  GROUPS_SECTION_DESCRIPTION,
  NO_GROUPS_RECORDED,
  PROFILE_RECORDED_CAPTION,
} from '../users/profileCopy'
import { NotFoundPage } from './NotFoundPage'

/**
 * A person's profile: the group memberships their sign-in carried, as the
 * server recorded them at their last sign-in.
 *
 * **Readable by every signed-in user, and that is a product decision, not a
 * gap.** The everyday use — a colleague checking which groups someone is in
 * before writing a grant or a restriction against one — is worth a page that
 * names them one person at a time. Nationality, email and last-seen are NOT
 * here: they stay on the audited admin roster.
 *
 * **Nothing that reads like a permission.** There is no clearance and no
 * selector eligibility, because this deployment has neither attribute: a
 * level is compared against nobody, and a selector is gated by the grants in
 * each space. Whether this person may read a particular page is a per-page
 * answer the permission inspector gives; a profile that summarised it would
 * be a second, stale answer.
 *
 * **Nothing here is edited here.** Every value is Keycloak's, mirrored at
 * sign-in, and the caption says so. There is no timestamp on purpose: the
 * page says "last sign-in" in words and no more precisely than that.
 *
 * Null from the server — an id that matches nobody — takes the app's
 * not-found treatment, the same screen an unknown page gets. A read that
 * never arrived is a different fact and says so.
 */
export function UserProfilePage() {
  const { userId } = useParams<{ userId: string }>()
  const groupsHeadingId = useId()
  const [{ data, fetching, error }] = useUserProfileQuery({
    variables: { id: userId ?? '' },
    pause: !userId,
  })
  const profile = data?.userProfile ?? null
  // The person's name is the tab title; the route's own "Profile" stands in
  // until it arrives (app/routeCrumbs.ts).
  useDocumentTitle(profile?.displayName)

  // FIRST LOAD ONLY. urql retains `data` across a refetch and flips `fetching`
  // true, so a bare `if (fetching)` would throw the rendered screen away on
  // every re-read; `&& !data` keeps it up while the re-read happens underneath.
  if (fetching && !data) {
    return (
      <Stack spacing={1}>
        <Skeleton variant="text" width="40%" height={48} />
        <Skeleton variant="rectangular" height={160} />
      </Stack>
    )
  }

  if (error) {
    // The absence of an answer, not a null one: a reader told "no such user"
    // for a request that never arrived would try the link again believing the
    // account gone.
    return <Alert severity="info">{describeLoadFailure('USER_PROFILE').summary}</Alert>
  }

  if (!profile) {
    return <NotFoundPage />
  }

  return (
    <Stack spacing={3}>
      <PageHeader
        title={profile.displayName}
        // Decorative beside the heading: the name IS the heading, so the face
        // is hidden from assistive tech rather than announced a second time.
        titleAdornment={
          <UserAvatar
            userId={profile.id}
            hasAvatar={profile.hasAvatar}
            displayName={profile.displayName}
            size={48}
            aria-hidden
          />
        }
      />

      {profile.isExternal ? (
        // A shadow account created by sync. Nothing below the name was ever
        // recorded, so say why rather than render an empty section.
        <Alert severity="info">{EXTERNAL_ACCOUNT_NOTE}</Alert>
      ) : (
        <>
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Stack spacing={1}>
              <Typography variant="h6" component="h2" id={groupsHeadingId}>
                Groups
              </Typography>
              <Typography variant="body2" color="text.secondary">
                {GROUPS_SECTION_DESCRIPTION}
              </Typography>
              {profile.groups.length === 0 ? (
                <Typography color="text.secondary">{NO_GROUPS_RECORDED}</Typography>
              ) : (
                // A list rather than chips: a group name is the thing a grant
                // or a restriction is written against, so each is a plain row
                // a reader can select and copy exactly, and a screen reader
                // hears how many there are. In the server's order, which is
                // ordinal — not re-sorted here.
                <List dense disablePadding aria-labelledby={groupsHeadingId}>
                  {profile.groups.map((name) => (
                    <ListItem key={name} disableGutters sx={{ py: 0.25 }}>
                      <ListItemText primary={name} />
                    </ListItem>
                  ))}
                </List>
              )}
            </Stack>
          </Paper>

          <Typography variant="body2" color="text.secondary">
            {PROFILE_RECORDED_CAPTION}
          </Typography>
        </>
      )}
    </Stack>
  )
}
