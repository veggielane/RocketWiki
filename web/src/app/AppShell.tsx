import { useState } from 'react'
import { Outlet, Link as RouterLink, useLocation, useNavigation } from 'react-router-dom'
import {
  AppBar,
  Box,
  Divider,
  Drawer,
  IconButton,
  InputAdornment,
  LinearProgress,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Menu,
  MenuItem,
  TextField,
  Toolbar,
  Tooltip,
  Typography,
} from '@mui/material'
import MenuIcon from '@mui/icons-material/Menu'
import SearchIcon from '@mui/icons-material/Search'
import Brightness4Icon from '@mui/icons-material/Brightness4'
import Brightness7Icon from '@mui/icons-material/Brightness7'
import SpaceDashboardOutlinedIcon from '@mui/icons-material/SpaceDashboardOutlined'
import AdminPanelSettingsOutlinedIcon from '@mui/icons-material/AdminPanelSettingsOutlined'
import SettingsOutlinedIcon from '@mui/icons-material/SettingsOutlined'
import LogoutIcon from '@mui/icons-material/Logout'
import { useAuth } from 'react-oidc-context'
import { useNavigate } from 'react-router-dom'
import { useColorMode } from '../theme/colorModeContext'
import { SpaceTreeNav } from './SpaceTreeNav'
import { CLASSIFICATION_BANNER_HEIGHT } from '../markings/ClassificationBanner'
import { NotificationBell } from '../notifications/NotificationBell'
import { useCurrentUserQuery } from '../graphql/generated/graphql'
import { UserAvatar } from '../avatars/UserAvatar'
import { useEmojiRegistryFeed } from '../emoji/useEmojiRegistry'
import { AskWikiEntryButton } from '../ask/AskWikiEntryButton'

const DRAWER_WIDTH = 280

export function AppShell() {
  const [drawerOpen, setDrawerOpen] = useState(true)
  const [userMenuAnchor, setUserMenuAnchor] = useState<HTMLElement | null>(null)
  const { mode, toggle } = useColorMode()
  const auth = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  // Routes now load their page component lazily (router.tsx) so the initial
  // bundle isn't paying for every route up front — this is the visible
  // trade-off for that: a route whose chunk isn't cached yet has a brief gap
  // between click and render. Surfacing it beats a navigation that appears
  // to silently do nothing.
  const navigation = useNavigation()
  // `me` supplies the local user id + hasAvatar for the account button
  // (design.md §19: the render decision is the flag, never a probing GET).
  const [{ data: meData }] = useCurrentUserQuery()
  // One feed for the custom-emoji registry (emoji/registry.ts) — every
  // editor/picker below the shell reads the module store.
  useEmojiRegistryFeed()

  const handleSearchSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const query = new FormData(e.currentTarget).get('q')
    if (typeof query === 'string' && query.trim().length > 0) {
      navigate(`/search?q=${encodeURIComponent(query.trim())}`)
    }
  }

  return (
    <Box sx={{ display: 'flex', height: '100vh' }}>
      {/* Keyboard-only until focused — lets a keyboard/screen-reader user
          skip the app bar and drawer's many tab stops on every single
          navigation, rather than tabbing through them each time. */}
      <Box
        component="a"
        href="#main-content"
        sx={{
          position: 'absolute',
          left: -9999,
          top: 'auto',
          zIndex: (theme) => theme.zIndex.tooltip + 1,
          p: 1.5,
          bgcolor: 'background.paper',
          color: 'text.primary',
          '&:focus': { left: 8, top: 8, position: 'fixed' },
        }}
      >
        Skip to main content
      </Box>
      <AppBar position="fixed" sx={{ zIndex: (theme) => theme.zIndex.drawer + 1 }} color="inherit" enableColorOnDark>
        <Toolbar sx={{ gap: 1 }}>
          <IconButton
            edge="start"
            aria-label={drawerOpen ? 'Collapse navigation' : 'Expand navigation'}
            onClick={() => setDrawerOpen((v) => !v)}
          >
            <MenuIcon />
          </IconButton>
          <Typography
            variant="h6"
            component={RouterLink}
            to="/"
            sx={{ textDecoration: 'none', color: 'inherit', fontWeight: 700, mr: 2 }}
          >
            RocketWiki
          </Typography>

          <Box component="form" onSubmit={handleSearchSubmit} sx={{ flexGrow: 1, maxWidth: 480 }}>
            <TextField
              name="q"
              size="small"
              fullWidth
              placeholder="Search…"
              slotProps={{
                input: {
                  startAdornment: (
                    <InputAdornment position="start">
                      <SearchIcon fontSize="small" />
                    </InputAdornment>
                  ),
                },
                // On the <input> itself, not the TextField: a root-level
                // aria-label lands on the FormControl div, where the generic
                // role prohibits it (axe aria-prohibited-attr, WCAG 4.1.2).
                htmlInput: { 'aria-label': 'Search the wiki' },
              }}
            />
          </Box>

          {/* Next to search: the two "find something" affordances live
              together. Self-hiding once the session learns the assistant
              is NOT_CONFIGURED (ask/askAvailability.ts). */}
          <AskWikiEntryButton />

          <Box sx={{ flexGrow: 1 }} />

          <Tooltip title={mode === 'light' ? 'Switch to dark mode' : 'Switch to light mode'}>
            <IconButton onClick={toggle} aria-label="Toggle color mode">
              {mode === 'light' ? <Brightness4Icon /> : <Brightness7Icon />}
            </IconButton>
          </Tooltip>

          <NotificationBell />

          <Tooltip title="Admin">
            <IconButton component={RouterLink} to="/admin" aria-label="Admin">
              <AdminPanelSettingsOutlinedIcon />
            </IconButton>
          </Tooltip>

          <Tooltip title={auth.user?.profile.name ?? 'Account'}>
            <IconButton onClick={(e) => setUserMenuAnchor(e.currentTarget)} aria-label="Account menu">
              <UserAvatar
                userId={meData?.me.localUserId}
                hasAvatar={meData?.me.hasAvatar}
                displayName={meData?.me.name ?? auth.user?.profile.name ?? '?'}
                size={32}
              />
            </IconButton>
          </Tooltip>
          <Menu anchorEl={userMenuAnchor} open={Boolean(userMenuAnchor)} onClose={() => setUserMenuAnchor(null)}>
            <MenuItem disabled>{auth.user?.profile.email ?? 'Not signed in'}</MenuItem>
            <Divider />
            <MenuItem
              onClick={() => {
                setUserMenuAnchor(null)
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
                setUserMenuAnchor(null)
                void auth.signoutRedirect()
              }}
            >
              <ListItemIcon>
                <LogoutIcon fontSize="small" />
              </ListItemIcon>
              Sign out
            </MenuItem>
          </Menu>
        </Toolbar>
        {navigation.state !== 'idle' && (
          <LinearProgress sx={{ position: 'absolute', bottom: 0, left: 0, right: 0 }} aria-label="Loading page" />
        )}
      </AppBar>

      <Drawer
        variant="persistent"
        open={drawerOpen}
        sx={{
          width: drawerOpen ? DRAWER_WIDTH : 0,
          flexShrink: 0,
          '& .MuiDrawer-paper': { width: DRAWER_WIDTH, boxSizing: 'border-box' },
        }}
      >
        <Toolbar />
        <List component="nav" aria-label="Spaces and pages">
          <ListItemButton component={RouterLink} to="/" selected={location.pathname === '/'}>
            <ListItemIcon>
              <SpaceDashboardOutlinedIcon />
            </ListItemIcon>
            <ListItemText primary="All spaces" />
          </ListItemButton>
        </List>
        <Divider />
        <SpaceTreeNav />
      </Drawer>

      <Box
        component="main"
        id="main-content"
        tabIndex={-1}
        sx={{
          flexGrow: 1,
          overflow: 'auto',
          bgcolor: 'background.default',
          outline: 'none',
          // WCAG 2.4.11 (focus not obscured): the app bar is position:fixed
          // over the top of this scroll container, so when the browser
          // scrolls a focused element into view it could land underneath it.
          // scroll-padding keeps keyboard-focus targets below the bar.
          scrollPaddingTop: '80px',
        }}
      >
        <Toolbar />
        {/* Bottom padding reserves the fixed classification banner's strip so a
            page's last line is never hidden underneath it. Applied here rather
            than per-route because the banner is viewport-fixed: it overlaps
            whatever is scrolled to the bottom, marked page or not. */}
        <Box sx={{ maxWidth: 960, mx: 'auto', p: 3, pb: `${CLASSIFICATION_BANNER_HEIGHT + 24}px` }}>
          <Outlet />
        </Box>
      </Box>
    </Box>
  )
}
