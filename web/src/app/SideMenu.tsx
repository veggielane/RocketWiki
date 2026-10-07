import { useEffect } from 'react'
import { Box, Drawer, List, ListItemButton, ListItemIcon, ListItemText, ListSubheader, Typography } from '@mui/material'
import HomeOutlinedIcon from '@mui/icons-material/HomeOutlined'
import HubOutlinedIcon from '@mui/icons-material/HubOutlined'
import RocketLaunchIcon from '@mui/icons-material/RocketLaunch'
import SpaceDashboardOutlinedIcon from '@mui/icons-material/SpaceDashboardOutlined'
import { Link as RouterLink, useLocation } from 'react-router-dom'
import { PRINT_HIDDEN } from '../theme/print'
import { SpaceTreeNav } from './SpaceTreeNav'
import { RecentSpaces } from './RecentSpaces'

export const SIDE_MENU_WIDTH = 272

/**
 * The sidebar's menu treatment, in the shape documentation sites give it:
 * small bold group titles, compact rounded rows, and a current item that is a
 * solid pill in the text colour with the surface colour on it — the one row
 * you cannot miss, which is the point of a sidebar that lists everything.
 *
 * Scoped to the sidebar through the drawer's `sx` rather than written into the
 * theme: `ListItemButton` is also search hits and page lists elsewhere, where
 * a selected row is a transient choice and an inverted pill would shout.
 *
 * The inversion is `text.primary` on `background.paper` on purpose — not a
 * brand tint — so it holds its contrast in both palettes without a second set
 * of numbers: near-black on white in light mode, white on near-black in dark.
 */
const SIDEBAR_MENU_SX = {
  '& .MuiListSubheader-root': {
    px: 1.5,
    pt: 2,
    pb: 0.5,
    fontSize: '0.75rem',
    fontWeight: 700,
    lineHeight: 1.5,
    color: 'text.secondary',
  },
  '& .MuiListItemButton-root': {
    minHeight: 32,
    px: 1.5,
    py: 0.5,
    borderRadius: 1,
  },
  '& .MuiListItemButton-root.Mui-selected': {
    bgcolor: 'text.primary',
    color: 'background.paper',
    '&:hover': { bgcolor: 'text.primary' },
    // Both the wrapper and the glyph: `ListItemIcon` sets its own colour
    // (`action.active`), so a glyph told to inherit would take that, not the
    // pill's, and vanish into it.
    '& .MuiListItemIcon-root, & .MuiSvgIcon-root': { color: 'inherit' },
    '& .MuiListItemText-primary': { color: 'inherit' },
    '& .MuiListItemText-secondary': { color: 'inherit', opacity: 0.75 },
  },
} as const

/**
 * The sidebar: the navigation column under the navbar, laid out the way a
 * documentation site lays out its drawer — groups with small titles, compact
 * rows, the current one an unmissable pill — and scrolling on its own, so a
 * long page tree never moves the content beside it.
 *
 * Top to bottom it reads all → recent → current: the wiki's whole-instance
 * views (home, every space, the document graph), the few spaces you keep
 * returning to, then the space you are in with its page tree. Identity is not
 * here: the brand sits in the navbar and the signed-in user behind its avatar
 * (AppHeader.tsx), which is where a docs site keeps them, and which frees the
 * whole column for navigation.
 *
 * `persistent` at desktop widths, collapsible from the navbar's toggle, and
 * `temporary` below `md` — an overlay with a backdrop that closes on
 * selection. A persistent 272px column on a 375px phone would leave nothing
 * usable beside it. The variant is decided by the shell (AppShell.tsx owns the
 * media query) so there is one answer to "are we compact" for the whole frame.
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

  // An overlay that stayed open over the page you just chose would hide it.
  // Keyed on the route rather than on a click handler: the sidebar is full of
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
      // Keeps the sidebar's DOM (and its tree state) mounted across a
      // phone-width open/close cycle rather than refetching every expanded branch.
      ModalProps={{ keepMounted: true }}
      sx={{
        // A temporary drawer sits over the content and must not also reserve
        // width beside it.
        width: !temporary && open ? SIDE_MENU_WIDTH : 0,
        flexShrink: 0,
        // Navigation does not print (theme/print.ts): on paper the sidebar is
        // a column of links nobody can follow, beside every sheet.
        ...PRINT_HIDDEN,
        '& .MuiDrawer-paper': {
          width: SIDE_MENU_WIDTH,
          boxSizing: 'border-box',
          borderRight: '1px solid',
          borderColor: 'divider',
          // MUI fixes the paper to the viewport for every variant, top to
          // bottom. Positioned within the shell's row instead (AppShell.tsx
          // makes that row the containing block), so the sidebar starts under
          // the navbar and ends above the classification banner — the rows
          // above and below it. The temporary overlay keeps MUI's fixed paper:
          // it is a modal, and like every modal it sits over both.
          ...(temporary ? {} : { position: 'absolute' }),
          ...SIDEBAR_MENU_SX,
        },
      }}
    >
      {/* The overlay covers the navbar, brand and all, so it carries the mark
          itself — the same thing a docs site's mobile drawer does. At desktop
          widths the navbar beside the sidebar already says whose sidebar it is. */}
      {temporary && (
        <Box
          component={RouterLink}
          to="/"
          aria-label="RocketWiki home"
          sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 1,
            height: 56,
            px: 2,
            flexShrink: 0,
            borderBottom: '1px solid',
            borderColor: 'divider',
            textDecoration: 'none',
            color: 'inherit',
          }}
        >
          <Box
            sx={{
              width: 28,
              height: 28,
              borderRadius: 1,
              display: 'grid',
              placeItems: 'center',
              bgcolor: 'primary.main',
              color: 'primary.contrastText',
            }}
          >
            <RocketLaunchIcon sx={{ fontSize: 18 }} />
          </Box>
          <Typography variant="body1" sx={{ fontWeight: 700, letterSpacing: '-0.01em' }}>
            RocketWiki
          </Typography>
        </Box>
      )}

      <Box sx={{ overflow: 'auto', flexGrow: 1, display: 'flex', flexDirection: 'column', px: 1, pb: 2 }}>
        <List
          component="nav"
          aria-label="Main"
          dense
          disablePadding
          subheader={
            <ListSubheader component="div" disableSticky>
              Wiki
            </ListSubheader>
          }
        >
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
          {/* The document graph, instance-wide by default (design.md §6.7).
              Beside "All spaces" because it is the other whole-instance
              view of the wiki's content. */}
          <ListItemButton component={RouterLink} to="/graph" selected={location.pathname === '/graph'}>
            <ListItemIcon>
              <HubOutlinedIcon />
            </ListItemIcon>
            <ListItemText primary="Graph" />
          </ListItemButton>
        </List>
        {/* all → recent → current: every space, the few you keep coming
            back to, then the one you are in (the picker and tree below). */}
        <RecentSpaces />
        <SpaceTreeNav />
      </Box>
    </Drawer>
  )
}
