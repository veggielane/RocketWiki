import { useParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Paper,
  Skeleton,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import CheckCircleOutlinedIcon from '@mui/icons-material/CheckCircleOutlined'
import GroupsOutlinedIcon from '@mui/icons-material/GroupsOutlined'
import HighlightOffOutlinedIcon from '@mui/icons-material/HighlightOffOutlined'
import { useUserProfileQuery } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { UserAvatar } from '../avatars/UserAvatar'
import { ClearanceBadge } from '../markings/ClearanceBadge'
import {
  CLEARANCE_NOT_RECORDED,
  CLEARANCE_NOT_RECORDED_DETAIL,
  EXTERNAL_ACCOUNT_NOTE,
  NO_SELECTOR_CATEGORIES,
  PROFILE_RECORDED_CAPTION,
  SELECTOR_SECTION_DESCRIPTION,
  describeEligibility,
  eligibilityKind,
  type EligibilityStatus,
} from '../users/profileCopy'
import { NotFoundPage } from './NotFoundPage'

/**
 * The accent beside each eligibility row. Decorative on purpose: MUI's icons
 * are `aria-hidden` by default and the tone is a palette token, so a reader
 * with neither colour nor pictures gets the row's text and loses nothing
 * (WCAG 1.4.1). The kind comes from the same function the text does, so the
 * two cannot disagree.
 */
function EligibilityIcon({ status }: { status: EligibilityStatus }) {
  switch (eligibilityKind(status)) {
    case 'EVERYONE':
      return <GroupsOutlinedIcon fontSize="small" sx={{ color: 'text.secondary' }} />
    case 'ELIGIBLE':
      return <CheckCircleOutlinedIcon fontSize="small" sx={{ color: 'success.main' }} />
    case 'NOT_ELIGIBLE':
      return <HighlightOffOutlinedIcon fontSize="small" sx={{ color: 'text.secondary' }} />
  }
}

/**
 * A person's profile: their clearance and their site-wide selector
 * eligibility per category, as the gate would read them from the claims
 * their last sign-in carried.
 *
 * **Readable by every signed-in user, and that is a product decision, not a
 * gap.** The user directory deliberately carries no clearance because a list
 * of clearances is a census; this page is that census one person at a time,
 * accepted because the everyday use — a colleague checking whether someone
 * may be shown a level or a compartment before sharing it — is worth it.
 * Nationality, email and last-seen are NOT here: they stay on the audited
 * admin roster.
 *
 * **Nothing here is edited here.** Every value is Keycloak's, mirrored at
 * sign-in, and the caption says so. There is no timestamp on purpose: the
 * page says "last sign-in" in words and no more precisely than that.
 *
 * **The floor is never shown as a clearance.** An absent or unrecognised
 * claim resolves on the wire to OFFICIAL-SENSITIVE — that is what the gate
 * does with it — but `clearanceRecorded: false` means the level is the
 * gate's default, not a fact about this person, and the page says "Not
 * recorded" in the badge's place (users/profileCopy.ts).
 *
 * Null from the server — an id that matches nobody — takes the app's
 * not-found treatment, the same screen an unknown page gets. A read that
 * never arrived is a different fact and says so.
 */
export function UserProfilePage() {
  const { userId } = useParams<{ userId: string }>()
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
        <Skeleton variant="rectangular" height={120} />
        <Skeleton variant="rectangular" height={200} />
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

  const rows = profile.selectorEligibility

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
        // recorded, so say why rather than render two empty sections.
        <Alert severity="info">{EXTERNAL_ACCOUNT_NOTE}</Alert>
      ) : (
        <>
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Stack spacing={1.5}>
              <Typography variant="h6" component="h2">
                Clearance
              </Typography>
              {profile.clearanceRecorded ? (
                <Box>
                  <ClearanceBadge level={profile.clearance} levelName={profile.clearanceName} />
                </Box>
              ) : (
                <Stack spacing={0.5}>
                  <Typography>{CLEARANCE_NOT_RECORDED}</Typography>
                  <Typography variant="body2" color="text.secondary">
                    {CLEARANCE_NOT_RECORDED_DETAIL}
                  </Typography>
                </Stack>
              )}
            </Stack>
          </Paper>

          <Paper variant="outlined">
            <Stack spacing={0.5} sx={{ p: 2, pb: rows.length === 0 ? 1 : 2 }}>
              <Typography variant="h6" component="h2">
                Selectors
              </Typography>
              <Typography variant="body2" color="text.secondary">
                {SELECTOR_SECTION_DESCRIPTION}
              </Typography>
            </Stack>
            {rows.length === 0 ? (
              <Typography color="text.secondary" sx={{ px: 2, pb: 2 }}>
                {NO_SELECTOR_CATEGORIES}
              </Typography>
            ) : (
              <Table size="small" aria-label="Selector eligibility">
                <TableHead>
                  <TableRow>
                    <TableCell>Category</TableCell>
                    <TableCell>Eligibility</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {rows.map((row) => (
                    <TableRow key={row.category}>
                      {/* The category names the row, so it is the row header
                          rather than a data cell — a screen reader moving down
                          the second column hears which category each answer
                          is for. */}
                      <TableCell component="th" scope="row" sx={{ fontWeight: 500 }}>
                        {row.category}
                      </TableCell>
                      <TableCell>
                        <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center' }}>
                          <EligibilityIcon status={row} />
                          <span>{describeEligibility(row)}</span>
                        </Stack>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </Paper>

          <Typography variant="body2" color="text.secondary">
            {PROFILE_RECORDED_CAPTION}
          </Typography>
        </>
      )}
    </Stack>
  )
}
