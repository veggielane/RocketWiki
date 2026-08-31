import { useEffect, useMemo, useSyncExternalStore } from 'react'
import { Link as RouterLink, useLocation } from 'react-router-dom'
import { List, ListItemButton, ListItemIcon, ListItemText, ListSubheader, Typography } from '@mui/material'
import HistoryOutlinedIcon from '@mui/icons-material/HistoryOutlined'
import { useSpaceListQuery } from '../graphql/generated/graphql'
import { sameSpaceKey } from '../pages/pageSlug'
import { replicaBadgeLabel } from '../feedback/unavailableCopy'
import {
  RECENT_SPACES_SHOWN,
  getRecentSpaceKeys,
  recordSpaceVisit,
  subscribeToRecentSpaces,
} from '../spaces/recentSpaces'
import { useCanonicalSpace } from './useCanonicalSpaceKey'

/**
 * Somewhere to jump back to.
 *
 * **localStorage supplies the ORDER; the space list supplies membership and
 * everything shown.** That split is the permission story and it is load-bearing
 * (§6.7): the rail already holds the server-filtered list of spaces this caller
 * may view, so intersecting against it means a key you can no longer see simply
 * has no match and vanishes. There is no branch that "hides" it — a stale key
 * renders nothing because there is nothing to render it from, which is a
 * stronger guarantee than a check somebody could forget to write.
 *
 * The space you are IN is excluded. The picker directly below shows it as its
 * selected value and the tree under that shows its pages, so listing it here
 * would be the same space three times down one narrow rail. Excluding it also
 * gives the section a single honest meaning: not "spaces you have used" but
 * "spaces that are one click away from here".
 *
 * The rail therefore reads all → recent → current: every space, the few you
 * keep returning to, then the one you are in.
 */
export function RecentSpaces() {
  const { pathname } = useLocation()
  // Resolves for `/spaces/{key}/…` AND `/pages/{id}`, from queries the rail
  // already runs — so being on a page records its space too, and the key is the
  // server's canonical spelling rather than whatever casing was typed.
  const currentSpace = useCanonicalSpace(pathname)
  const currentKey = currentSpace?.key
  const [{ data }] = useSpaceListQuery()

  // Writing is the side effect; the store's notification is what re-renders the
  // rail. Keyed on the resolved key, so navigating within one space records
  // once rather than on every page in it.
  useEffect(() => {
    if (currentKey !== undefined) recordSpaceVisit(currentKey)
  }, [currentKey])

  // `useSyncExternalStore`, not a `useState` seeded from storage: the rail does
  // not unmount between routes, so a value read once at mount would go stale
  // the moment you moved to another space.
  const recentKeys = useSyncExternalStore(subscribeToRecentSpaces, getRecentSpaceKeys)

  const spaces = data?.spaces
  const recent = useMemo(() => {
    const viewable = spaces ?? []
    return recentKeys
      .filter((key) => !sameSpaceKey(key, currentKey))
      .map((key) => viewable.find((space) => sameSpaceKey(space.key, key)))
      .filter((space): space is NonNullable<typeof space> => space !== undefined)
      .slice(0, RECENT_SPACES_SHOWN)
  }, [recentKeys, spaces, currentKey])

  // No header over an empty list. A new account has been nowhere, and a
  // "Recent" heading with nothing under it is a promise the rail cannot keep.
  if (recent.length === 0) return null

  return (
    <List
      component="nav"
      aria-labelledby="recent-spaces-heading"
      dense
      subheader={
        <ListSubheader component="div" id="recent-spaces-heading" disableSticky>
          Recent
        </ListSubheader>
      }
    >
      {recent.map((space) => (
        <ListItemButton key={space.key} component={RouterLink} to={`/spaces/${space.key}`}>
          <ListItemIcon>
            <HistoryOutlinedIcon fontSize="small" />
          </ListItemIcon>
          <ListItemText
            primary={space.name}
            secondary={
              // The same replica note the picker's rows carry, so a read-only
              // mirror is recognisable before you go there rather than after.
              space.isReplica ? (
                <Typography component="span" variant="caption" color="text.secondary">
                  {replicaBadgeLabel(space.originInstanceId)}
                </Typography>
              ) : undefined
            }
            slotProps={{ primary: { noWrap: true }, secondary: { component: 'div' } }}
          />
        </ListItemButton>
      ))}
    </List>
  )
}
