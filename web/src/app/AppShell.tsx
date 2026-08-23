import { useState } from 'react'
import { Outlet, Link as RouterLink, useLocation, useNavigation } from 'react-router-dom'
import {
  AppBar,
  Avatar,
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
import { NotificationBell } from '../notifications/NotificationBell'

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
              aria-label="Search the wiki"
              slotProps={{
                input: {
                  startAdornment: (
                    <InputAdornment position="start">
                      <SearchIcon fontSize="small" />
                    </InputAdornment>
                  ),
                },
              }}
            />
          </Box>

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
              <Avatar sx={{ width: 32, height: 32 }}>
                {(auth.user?.profile.name ?? '?').slice(0, 1).toUpperCase()}
              </Avatar>
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
        }}
      >
        <Toolbar />
        <Box sx={{ maxWidth: 960, mx: 'auto', p: 3 }}>
          <Outlet />
        </Box>
      </Box>
    </Box>
  )
}
