import { useMemo, useState } from 'react'
import {
  Badge,
  Box,
  IconButton,
  List,
  ListItem,
  ListItemButton,
  ListItemText,
  Popover,
  Tooltip,
  Typography,
} from '@mui/material'
import NotificationsOutlinedIcon from '@mui/icons-material/NotificationsOutlined'
import { Link as RouterLink } from 'react-router-dom'
import { useNotifications } from './useNotifications'
import { describeNotification } from './describeNotification'
import { getDefaultNotificationsTransport } from '../realtime/transports'
import { useMarkNotificationReadMutation, usePersistedNotificationsQuery } from '../graphql/generated/graphql'
import type { NotificationPayload, NotificationsTransport } from '../realtime/types'
import { formatTimestamp } from '../format/dateTime'
import { describeLoadFailure } from '../feedback/unavailableCopy'

export interface NotificationBellProps {
  /**
   * Injection point for tests; the app leaves it unset and gets the
   * app-lifetime singleton from realtime/transports.ts (the seam that
   * picks SignalR vs the fake). One instance for the app's lifetime —
   * recreating a transport per render would connect/disconnect on every
   * re-render.
   */
  transport?: NotificationsTransport
}

export function NotificationBell({ transport }: NotificationBellProps = {}) {
  const [anchorEl, setAnchorEl] = useState<HTMLElement | null>(null)
  const [{ data, fetching, error: notificationsFailed }] = usePersistedNotificationsQuery()
  const [, markNotificationRead] = useMarkNotificationReadMutation()

  // Lazily resolved so merely importing/rendering with an injected
  // transport (tests) never constructs a hub connection.
  const activeTransport = useMemo(() => transport ?? getDefaultNotificationsTransport(), [transport])

  const persisted: NotificationPayload[] = useMemo(
    () =>
      (data?.notifications ?? []).map((n) => ({
        id: n.id,
        type: n.type as NotificationPayload['type'],
        pageId: n.pageId,
        spaceKey: n.spaceKey,
        pageTitle: n.pageTitle,
        actorDisplayName: n.actorDisplayName,
        timestampUtc: n.createdAtUtc,
        readAtUtc: n.readAtUtc,
      })),
    [data],
  )

  const { notifications, unreadCount, markRead } = useNotifications(activeTransport, persisted)

  return (
    <>
      <Tooltip title="Notifications">
        <IconButton onClick={(e) => setAnchorEl(e.currentTarget)} aria-label={`Notifications (${unreadCount} unread)`}>
          <Badge badgeContent={unreadCount} color="error" max={99}>
            <NotificationsOutlinedIcon />
          </Badge>
        </IconButton>
      </Tooltip>
      {/* A Popover with a real list, NOT a Menu: role="menu" may only
          contain menuitems, and these entries are a list of notification
          links — nesting a <ul> of <div>s inside a menu was an axe
          aria-required-children/list violation (WCAG 1.3.1/4.1.2). */}
      <Popover
        anchorEl={anchorEl}
        open={Boolean(anchorEl)}
        onClose={() => setAnchorEl(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'top', horizontal: 'right' }}
      >
        {/* Three states, not two. `fetching` and `error` were both discarded,
            so during the initial read — and permanently if it failed — the
            popover asserted "No notifications", which is a claim about the
            inbox made without having read it. Same class as the form fences.
            The wrapper carries the accessible name in every state: the empty
            and failed states render no `List`, so the popover otherwise had no
            name at all when it was not populated. */}
        <Box sx={{ minWidth: 320, maxWidth: 400 }} role="group" aria-label="Notifications">
          {fetching && notifications.length === 0 ? (
            <Typography variant="body2" color="text.secondary" sx={{ px: 2, py: 1.5 }}>
              Loading notifications…
            </Typography>
          ) : notificationsFailed && notifications.length === 0 ? (
            <Typography variant="body2" color="text.secondary" sx={{ px: 2, py: 1.5 }}>
              {describeLoadFailure('NOTIFICATIONS').summary}
            </Typography>
          ) : notifications.length === 0 ? (
            <Typography variant="body2" color="text.secondary" sx={{ px: 2, py: 1.5 }}>
              No notifications — watch a page or space to hear when it changes.
            </Typography>
          ) : (
          <List dense>
            {notifications.map((n, i) => {
              const { headline, linkable } = describeNotification(n)
              const isUnread = !n.readAtUtc
              return (
                <ListItem key={n.id} disablePadding divider={i < notifications.length - 1}>
                  <ListItemButton
                    component={linkable ? RouterLink : 'div'}
                    to={linkable ? `/pages/${n.pageId}` : undefined}
                    onClick={() => {
                      // Server first (the persisted row is the record —
                      // design.md §8), local immediately: the badge
                      // shouldn't wait a round-trip, and the next
                      // `notifications` refetch carries readAtUtc anyway.
                      if (isUnread) {
                        void markNotificationRead({ input: { notificationId: n.id } })
                      }
                      markRead(n.id)
                      setAnchorEl(null)
                    }}
                  >
                    {/* Read rows de-emphasize via weight + the theme's
                        secondary text color — never via opacity, which
                        multiplies below the WCAG 1.4.3 contrast floor. */}
                    <ListItemText
                      primary={headline}
                      secondary={formatTimestamp(n.timestampUtc)}
                      slotProps={{
                        primary: {
                          sx: { fontWeight: isUnread ? 600 : 400, color: isUnread ? 'text.primary' : 'text.secondary' },
                        },
                      }}
                    />
                  </ListItemButton>
                </ListItem>
              )
            })}
            </List>
          )}
        </Box>
      </Popover>
    </>
  )
}
