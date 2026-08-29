import { useState } from 'react'
import {
  Box,
  Divider,
  Drawer,
  IconButton,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Menu,
  MenuItem,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import LogoutIcon from '@mui/icons-material/Logout'
import MoreVertIcon from '@mui/icons-material/MoreVert'
import RocketLaunchIcon from '@mui/icons-material/RocketLaunch'
import SettingsOutlinedIcon from '@mui/icons-material/SettingsOutlined'
import SpaceDashboardOutlinedIcon from '@mui/icons-material/SpaceDashboardOutlined'
import { Link as RouterLink, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from 'react-oidc-context'
import { UserAvatar } from '../avatars/UserAvatar'
import { useCurrentUserQuery } from '../graphql/generated/graphql'
import { CLASSIFICATION_BANNER_HEIGHT } from '../markings/ClassificationBanner'
import { SpaceTreeNav } from './SpaceTreeNav'

export const SIDE_MENU_WIDTH = 240

/**
 * The navigation rail, following the MUI Dashboard template's side menu: an
 * identity block at the top, a divider, the navigation in a scrolling middle,
 * and the signed-in user pinned to the bottom above a rule.
 *
 * The template puts a product picker in the top block. Here the space picker
 * that would answer to it is the first thing `SpaceTreeNav` renders, so the top
 * block carries the product mark instead — which is also where the mark has to
 * live now that there is no desktop app bar to hold it.
 *
 * `persistent` rather than the template's `permanent`, because collapsing the
 * rail is an affordance this app already had and the template simply has no
 * equivalent of; the toggle lives in the header strip (AppHeader.tsx).
 */
export function SideMenu({ open }: { open: boolean }) {
  const location = useLocation()
  const navigate = useNavigate()
  const auth = useAuth()
  const [accountMenuAnchor, setAccountMenuAnchor] = useState<HTMLElement | null>(null)
  // `me` supplies the local user id + hasAvatar for the account block
  // (design.md §19: the render decision is the flag, never a probing GET).
  const [{ data: meData }] = useCurrentUserQuery()

  const displayName = meData?.me.name ?? auth.user?.profile.name ?? 'Account'
  const email = auth.user?.profile.email

  return (
    <Drawer
      variant="persistent"
      open={open}
      sx={{
        width: open ? SIDE_MENU_WIDTH : 0,
        flexShrink: 0,
        '& .MuiDrawer-paper': {
          width: SIDE_MENU_WIDTH,
          boxSizing: 'border-box',
          // The classification banner is fixed across the whole viewport
          // bottom, rail included, so the account block has to reserve its
          // strip the same way the content region does — the alternative is a
          // security marking sitting on top of the signed-in user's name.
          paddingBottom: `${CLASSIFICATION_BANNER_HEIGHT}px`,
        },
      }}
    >
      <Box
        component={RouterLink}
        to="/"
        sx={{ display: 'flex', alignItems: 'center', gap: 1, p: 1.5, textDecoration: 'none', color: 'inherit' }}
      >
        {/* The template's brand mark, flat rather than its gradient: a gradient
            is a background-image, and the discipline that keeps Paper flat
            (theme/componentCustomizations.ts) is worth holding to even where no
            text sits on top. */}
        <Box
          sx={{
            width: 28,
            height: 28,
            borderRadius: 1,
            display: 'grid',
            placeItems: 'center',
            bgcolor: 'primary.main',
            color: 'primary.contrastText',
            flexShrink: 0,
          }}
        >
          <RocketLaunchIcon sx={{ fontSize: 18 }} />
        </Box>
        <Typography variant="body2" sx={{ fontWeight: 700 }}>
          RocketWiki
        </Typography>
      </Box>
      <Divider />

      <Box sx={{ overflow: 'auto', flexGrow: 1, display: 'flex', flexDirection: 'column' }}>
        <List component="nav" aria-label="Spaces">
          <ListItemButton component={RouterLink} to="/" selected={location.pathname === '/'}>
            <ListItemIcon>
              <SpaceDashboardOutlinedIcon />
            </ListItemIcon>
            <ListItemText primary="All spaces" />
          </ListItemButton>
        </List>
        <SpaceTreeNav />
      </Box>

      <Stack
        direction="row"
        sx={{ p: 2, gap: 1, alignItems: 'center', borderTop: '1px solid', borderColor: 'divider' }}
      >
        <UserAvatar
          userId={meData?.me.localUserId}
          hasAvatar={meData?.me.hasAvatar}
          displayName={displayName}
          size={36}
        />
        <Box sx={{ mr: 'auto', minWidth: 0 }}>
          <Typography variant="body2" noWrap sx={{ fontWeight: 500, lineHeight: '16px' }}>
            {displayName}
          </Typography>
          <Typography variant="caption" noWrap sx={{ color: 'text.secondary', display: 'block' }}>
            {email ?? 'Not signed in'}
          </Typography>
        </Box>
        <Tooltip title="Account menu">
          <IconButton
            aria-label="Account menu"
            onClick={(e) => setAccountMenuAnchor(e.currentTarget)}
            sx={{ flexShrink: 0 }}
          >
            <MoreVertIcon />
          </IconButton>
        </Tooltip>
        <Menu
          anchorEl={accountMenuAnchor}
          open={Boolean(accountMenuAnchor)}
          onClose={() => setAccountMenuAnchor(null)}
          anchorOrigin={{ horizontal: 'right', vertical: 'top' }}
          transformOrigin={{ horizontal: 'right', vertical: 'bottom' }}
        >
          <MenuItem
            onClick={() => {
              setAccountMenuAnchor(null)
              navigate('/settings')
            }}
          >
            <ListItemIcon>
              <SettingsOutlinedIcon fontSize="small" />
            </ListItemIcon>
            Settings
          </MenuItem>
          <MenuItem
            onClick={() => {
              setAccountMenuAnchor(null)
              void auth.signoutRedirect()
            }}
          >
            <ListItemIcon>
              <LogoutIcon fontSize="small" />
            </ListItemIcon>
            Sign out
          </MenuItem>
        </Menu>
      </Stack>
    </Drawer>
  )
}
