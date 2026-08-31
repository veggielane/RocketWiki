import { useState } from 'react'
import { Alert, Button, Paper, Stack, Typography } from '@mui/material'
import { useSetSpaceOwnerMutation } from '../graphql/generated/graphql'
import { describeMutationError } from '../graphql/mutationError'
import { describeWriteFailure } from '../feedback/unavailableCopy'
import { UserAvatar } from '../avatars/UserAvatar'
import { UserDirectoryPicker, type DirectoryUser } from './UserDirectoryPicker'

export interface SpaceOwnerSectionProps {
  spaceId: string
  owner: { id: string; displayName: string; hasAvatar: boolean } | null
  /** The server's own answer, not a proxy: instance admin OR this space's space-admin. */
  canManageAccess: boolean
  /** Refetch the space after a successful reassignment. */
  onChanged: () => void
}

/**
 * Who is accountable for this space.
 *
 * **Ownership is not access, and the copy says so out loud.** design.md §6.5
 * keeps the two apart deliberately — grants decide who may read and write, the
 * owner is who answers for the space — and a UI that put them next to each
 * other without saying which is which would be the fastest way to have somebody
 * "fix" access by reassigning the owner. That sentence is the feature.
 *
 * **`owner: null` is a real state, not a loading one.** An imported replica
 * arrives ownerless (the empty GUID) because ownership never syncs, so the
 * high side has to take responsibility for imported content explicitly. The
 * screen therefore says "No owner assigned" rather than spinning, and offers
 * to fix it to anyone who may.
 *
 * **Replicas are not excluded.** Every other write on this screen is disabled
 * for a replica, and this one deliberately is not: owner assignment is the one
 * space write exempt from the read-only rule, precisely because refusing it
 * would leave imported spaces permanently ownerless. The gate is
 * `canManageAccess` alone.
 */
export function SpaceOwnerSection({ spaceId, owner, canManageAccess, onChanged }: SpaceOwnerSectionProps) {
  const [{ fetching }, setSpaceOwner] = useSetSpaceOwnerMutation()
  const [choosing, setChoosing] = useState(false)
  const [chosen, setChosen] = useState<DirectoryUser | null>(null)
  const [error, setError] = useState<string | null>(null)

  const assign = async () => {
    if (!chosen) return
    setError(null)
    const result = await setSpaceOwner({ input: { spaceId, ownerUserId: chosen.id } })
    // A transport failure carries no payload, so the typed-error helper returns
    // null and the button would appear to do nothing.
    if (result.error !== undefined) {
      setError(describeWriteFailure('SPACE_OWNER').summary)
      return
    }
    // Forbidden / Validation (no such user) / NotFound all arrive here as the
    // server's own wording, which is the wording that explains the refusal.
    // Optional all the way down: the payload is non-null in the schema, but a
    // half-formed response reaching `.error` on `undefined` throws out of a
    // click handler, and a save that explodes is worse than one that reports.
    const refused = describeMutationError(result.data?.setSpaceOwner?.error)
    if (refused) {
      setError(refused)
      return
    }
    setChoosing(false)
    setChosen(null)
    onChanged()
  }

  const cancel = () => {
    setChoosing(false)
    setChosen(null)
    setError(null)
  }

  return (
    <Paper variant="outlined" sx={{ p: 2 }} data-testid="space-owner">
      <Stack spacing={2}>
        <Stack spacing={0.5}>
          <Typography variant="h6" component="h2">
            Owner
          </Typography>
          <Typography variant="body2" color="text.secondary">
            The person accountable for this space. Ownership does not grant access — who can read and edit is
            decided by this space's grants.
          </Typography>
        </Stack>

        {error && (
          <Alert severity="warning" onClose={() => setError(null)}>
            {error}
          </Alert>
        )}

        {owner ? (
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <UserAvatar userId={owner.id} hasAvatar={owner.hasAvatar} displayName={owner.displayName} size={32} />
            <Typography>{owner.displayName}</Typography>
          </Stack>
        ) : (
          // Said plainly, and to everyone. A gap in accountability is worth
          // knowing about even if you are not the person who can close it.
          <Typography color="text.secondary">
            No owner assigned. Spaces imported from another instance arrive without one, because ownership does
            not travel with the content.
          </Typography>
        )}

        {canManageAccess &&
          (choosing ? (
            <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start', flexWrap: 'wrap' }}>
              <UserDirectoryPicker
                label="New owner"
                value={chosen}
                onChange={setChosen}
                disabled={fetching}
                helperText="Anyone with an account. They are not given access by this."
              />
              <Button variant="contained" onClick={() => void assign()} disabled={!chosen || fetching} sx={{ mt: 0.5 }}>
                {fetching ? 'Saving…' : 'Save owner'}
              </Button>
              <Button onClick={cancel} disabled={fetching} sx={{ mt: 0.5 }}>
                Cancel
              </Button>
            </Stack>
          ) : (
            <Button variant={owner ? 'text' : 'outlined'} onClick={() => setChoosing(true)} sx={{ alignSelf: 'flex-start' }}>
              {owner ? 'Change owner' : 'Assign an owner'}
            </Button>
          ))}
      </Stack>
    </Paper>
  )
}
