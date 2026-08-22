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
import { FakeNotificationsTransport } from '../realtime/FakeNotificationsTransport'
import { usePersistedNotificationsQuery } from '../graphql/generated/graphql'
import type { NotificationPayload } from '../realtime/types'

// One transport instance for the app's lifetime — recreating it per render
// would connect/disconnect on every re-render. Swap for
// `SignalRNotificationsTransport` once `/hubs/notifications` exists
// (design.md §8, milestone 4b) — the fake is a deliberate placeholder here,
// not a workaround.
const transport = new FakeNotificationsTransport()

export function NotificationBell() {
  const [anchorEl, setAnchorEl] = useState<HTMLElement | null>(null)
  const [{ data }] = usePersistedNotificationsQuery()

  const persisted: NotificationPayload[] = useMemo(
    () =>
      (data?.notifications ?? []).map((n) => ({
        id: n.id,
        type: n.type as NotificationPayload['type'],
        pageId: n.pageId,
        spaceKey: n.spaceKey,
        pageTitle: n.pageTitle ?? null,
        actorDisplayName: n.actorDisplayName,
        timestampUtc: n.createdAtUtc,
        readAtUtc: n.readAtUtc ?? null,
      })),
    [data],
  )

  const { notifications, unreadCount, markRead } = useNotifications(transport, persisted)

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
