import { useMemo, useState } from 'react'
import {
  Badge,
  Divider,
  IconButton,
  List,
  ListItemButton,
  ListItemText,
  Menu,
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
  const [{ data }] = usePersistedNotificationsQuery()
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
      <Menu anchorEl={anchorEl} open={Boolean(anchorEl)} onClose={() => setAnchorEl(null)}>
        {notifications.length === 0 ? (
          <Typography variant="body2" color="text.secondary" sx={{ px: 2, py: 1.5 }}>
            No notifications.
          </Typography>
        ) : (
          <List dense sx={{ minWidth: 320, maxWidth: 400 }}>
            {notifications.map((n, i) => {
              const { headline, linkable } = describeNotification(n)
              const isUnread = !n.readAtUtc
              return (
                <div key={n.id}>
                  {i > 0 && <Divider component="li" />}
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
                    sx={{ opacity: isUnread ? 1 : 0.6 }}
                  >
                    <ListItemText
                      primary={headline}
                      secondary={new Date(n.timestampUtc).toLocaleString()}
                      slotProps={{ primary: { sx: { fontWeight: isUnread ? 600 : 400 } } }}
                    />
                  </ListItemButton>
                </div>
              )
            })}
          </List>
        )}
      </Menu>
    </>
  )
}
