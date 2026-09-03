import { useEffect, useState } from 'react'
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
import AccountCircleOutlinedIcon from '@mui/icons-material/AccountCircleOutlined'
import LogoutIcon from '@mui/icons-material/Logout'
import MoreVertIcon from '@mui/icons-material/MoreVert'
import RocketLaunchIcon from '@mui/icons-material/RocketLaunch'
import SettingsOutlinedIcon from '@mui/icons-material/SettingsOutlined'
import SpaceDashboardOutlinedIcon from '@mui/icons-material/SpaceDashboardOutlined'
import HomeOutlinedIcon from '@mui/icons-material/HomeOutlined'
import { Link as RouterLink, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from 'react-oidc-context'
import { UserAvatar } from '../avatars/UserAvatar'
import { useCurrentUserQuery } from '../graphql/generated/graphql'
import { CLASSIFICATION_BANNER_HEIGHT } from '../markings/ClassificationBanner'
import { SpaceTreeNav } from './SpaceTreeNav'
import { RecentSpaces } from './RecentSpaces'
import { clearRecentSpaces } from '../spaces/recentSpaces'
import { profilePath } from '../users/profilePath'

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
 *
 * Below `md` it becomes `temporary` instead — an overlay with a backdrop that
 * closes on selection. A persistent 240px rail on a 375px phone left about 87px
 * of content beside it, which is not a narrow layout so much as an unusable one.
 * The variant is decided by the shell (AppShell.tsx owns the media query) rather
 * than here, so there is one answer to "are we compact" for the whole frame.
 */
export function SideMenu({
  open,
  temporary = false,
  onClose,
}: {
  open: boolean
  temporary?: boolean
  onClose?: () => void
}) {
  const location = useLocation()
  const navigate = useNavigate()
  const auth = useAuth()
  const [accountMenuAnchor, setAccountMenuAnchor] = useState<HTMLElement | null>(null)
  // `me` supplies the local user id + hasAvatar for the account block
  // (design.md §19: the render decision is the flag, never a probing GET).
  const [{ data: meData }] = useCurrentUserQuery()

  const displayName = meData?.me.name ?? auth.user?.profile.name ?? 'Account'
  const email = auth.user?.profile.email

  // An overlay that stayed open over the page you just chose would hide it.
  // Keyed on the route rather than on a click handler: the rail is full of
  // controls that are NOT navigation (every disclosure chevron in the tree), and
  // a click handler on the drawer would fold the overlay away every time someone
  // expanded a branch to look for the page they wanted.
  useEffect(() => {
    if (temporary && open) onClose?.()
    // `onClose` is not a dependency: the shell recreates it per render, and
    // including it would close the drawer immediately on every re-render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [location.pathname])

  return (
    <Drawer
      variant={temporary ? 'temporary' : 'persistent'}
      open={open}
      onClose={onClose}
      // Keeps the rail's DOM (and its tree state) mounted across a phone-width
      // open/close cycle rather than refetching every expanded branch.
      ModalProps={{ keepMounted: true }}
      sx={{
        // A temporary drawer sits over the content and must not also reserve
        // width beside it.
        width: !temporary && open ? SIDE_MENU_WIDTH : 0,
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
        <List component="nav" aria-label="Main">
          <ListItemButton component={RouterLink} to="/" selected={location.pathname === '/'}>
            <ListItemIcon>
              <HomeOutlinedIcon />
            </ListItemIcon>
            <ListItemText primary="Home" />
          </ListItemButton>
          <ListItemButton component={RouterLink} to="/spaces" selected={location.pathname === '/spaces'}>
            <ListItemIcon>
              <SpaceDashboardOutlinedIcon />
            </ListItemIcon>
            <ListItemText primary="All spaces" />
          </ListItemButton>
        </List>
        {/* all → recent → current: every space, the few you keep coming
            back to, then the one you are in (the picker and tree below). */}
        <RecentSpaces />
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
          {/* Hidden, not disabled, until `me.localUserId` has arrived: the
              profile route takes the local id, and an item that navigated to
              /users/undefined would be a menu entry that leads to a 404. A
              user's own id is not a secret from them, so this is not the
              absent-rather-than-forbidden question — just a link that cannot
              be written yet. */}
          {meData?.me.localUserId && (
            <MenuItem
              component={RouterLink}
              to={profilePath(meData.me.localUserId)}
              onClick={() => setAccountMenuAnchor(null)}
            >
              <ListItemIcon>
                <AccountCircleOutlinedIcon fontSize="small" />
              </ListItemIcon>
              My profile
            </MenuItem>
          )}
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
              // The recent-spaces list is not a token, but it is a record of
              // where somebody has been, and on a shared workstation it would
              // outlive them — a key like OPBLACKSTAR tells the next person at
              // that browser such a programme exists. Signing out is the
              // explicit "I am done here", so it is the honest moment to drop it.
              clearRecentSpaces()
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
