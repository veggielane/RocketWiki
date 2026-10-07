import { useEffect, useRef, useState } from 'react'
import {
  Box,
  Divider,
  IconButton,
  InputAdornment,
  ListItemIcon,
  Menu,
  MenuItem,
  Stack,
  TextField,
  Tooltip,
  Typography,
  useMediaQuery,
  useTheme,
} from '@mui/material'
import AccountCircleOutlinedIcon from '@mui/icons-material/AccountCircleOutlined'
import AdminPanelSettingsOutlinedIcon from '@mui/icons-material/AdminPanelSettingsOutlined'
import HelpOutlineOutlinedIcon from '@mui/icons-material/HelpOutlineOutlined'
import Brightness4Icon from '@mui/icons-material/Brightness4'
import Brightness7Icon from '@mui/icons-material/Brightness7'
import LogoutIcon from '@mui/icons-material/Logout'
import MenuIcon from '@mui/icons-material/Menu'
import MoreVertIcon from '@mui/icons-material/MoreVert'
import RocketLaunchIcon from '@mui/icons-material/RocketLaunch'
import SearchIcon from '@mui/icons-material/Search'
import SettingsOutlinedIcon from '@mui/icons-material/SettingsOutlined'
import { Link as RouterLink, useLocation, useNavigate, useSearchParams } from 'react-router-dom'
import { useAuth } from 'react-oidc-context'
import { AskWikiEntryButton } from '../ask/AskWikiEntryButton'
import { NotificationBell } from '../notifications/NotificationBell'
import { UserAvatar } from '../avatars/UserAvatar'
import { useIsInstanceAdmin } from '../auth/useIsInstanceAdmin'
import { useCurrentUserQuery } from '../graphql/generated/graphql'
import { clearRecentSpaces } from '../spaces/recentSpaces'
import { useColorMode } from '../theme/colorModeContext'
import { PRINT_HIDDEN } from '../theme/print'
import { profilePath } from '../users/profilePath'

/** The navbar's height, in px. The sidebar is laid out beneath it (AppShell.tsx). */
export const APP_BAR_HEIGHT = 56

/**
 * Ghost buttons, the way a docs site's navbar draws them: a bare glyph that
 * grows a soft square on hover, no border. Applied here rather than to
 * `MuiIconButton` in the theme for the reason componentCustomizations.ts gives
 * for not adopting the template's bordered version: globally it would also
 * restyle every tree chevron and remove-row button.
 */
const NAVBAR_BUTTONS_SX = {
  '& .MuiIconButton-root': {
    width: 36,
    height: 36,
    borderRadius: 1,
    color: 'text.primary',
    '&:hover': { bgcolor: 'action.hover' },
  },
} as const

export interface AppHeaderProps {
  navOpen: boolean
  onToggleNav: () => void
}

/** `⌘` on a Mac keyboard, `Ctrl` elsewhere — the hint has to name the key the person actually has. */
function modifierKeyLabel(): string {
  if (typeof navigator === 'undefined') return 'Ctrl'
  return /mac|iphone|ipad/i.test(navigator.platform) ? '⌘' : 'Ctrl'
}

/** A keycap, the shape docs sites use to advertise a shortcut inside a search box. */
function Kbd({ children }: { children: string }) {
  return (
    <Box
      component="kbd"
      sx={{
        fontFamily: 'inherit',
        fontSize: '0.6875rem',
        fontWeight: 600,
        lineHeight: 1,
        px: 0.75,
        py: 0.5,
        borderRadius: 0.75,
        border: '1px solid',
        borderColor: 'divider',
        borderBottomWidth: 2,
        bgcolor: 'background.default',
        color: 'text.secondary',
        whiteSpace: 'nowrap',
      }}
    >
      {children}
    </Box>
  )
}

/**
 * The navbar: the full-width strip across the top of the frame, in the shape
 * a documentation site gives it. Left to right: the sidebar toggle, the brand
 * mark, a search box with its keyboard shortcut on show, and then the global
 * actions — Ask, notifications, theme, help, admin — ending in the signed-in
 * user's avatar, which opens the account menu (profile, settings, sign out).
 *
 * It is a row of the shell above the scroll region, not a bar fixed over it,
 * so nothing is ever obscured beneath it and the content region needs no
 * `scroll-padding` (WCAG 2.4.11 — docs/ACCESSIBILITY.md). It does not print:
 * every control here is furniture for a person at a screen (theme/print.ts).
 * The breadcrumb, which does print, is the first row of the content column.
 *
 * Below `sm` the search field and the low-frequency actions (theme, help,
 * admin) fold into an overflow menu: the cluster is seven controls plus a
 * search field, which a phone's width does not have. The bell and the avatar
 * never fold — an unread count you have to open a menu to see is not a
 * notification, and who you are signed in as should always be one press away.
 */
export function AppHeader({ navOpen, onToggleNav }: AppHeaderProps) {
  const navigate = useNavigate()
  const { pathname } = useLocation()
  const theme = useTheme()
  const isCompact = useMediaQuery(theme.breakpoints.down('sm'))
  const { mode, toggle } = useColorMode()
  const auth = useAuth()
  const [overflowAnchor, setOverflowAnchor] = useState<HTMLElement | null>(null)
  const [accountAnchor, setAccountAnchor] = useState<HTMLElement | null>(null)
  // The admin surfaces are instance-admin-only (router.tsx), and an ungated
  // button here sent everyone else to a 404. Hidden rather than disabled, which
  // is this app's posture for an absent capability everywhere else — the Ask
  // button beside it returns null the same way when the assistant is not
  // configured. A user's own role is not a secret from them, so this is not the
  // §6.7 read-path question.
  const { isInstanceAdmin } = useIsInstanceAdmin()
  // `me` supplies the local user id + hasAvatar for the account block
  // (design.md §19: the render decision is the flag, never a probing GET).
  const [{ data: meData }] = useCurrentUserQuery()
  const displayName = meData?.me.name ?? auth.user?.profile.name ?? 'Account'
  const email = auth.user?.profile.email

  // What the search page is currently showing, when that is where we are.
  const [searchParams] = useSearchParams()
  const currentQuery = pathname === '/search' ? (searchParams.get('q') ?? '') : ''

  const searchRef = useRef<HTMLInputElement>(null)

  // Ctrl/⌘+K lands in the search box from anywhere, which is what the keycap
  // in the box promises. Where the box has folded away (below `sm`) the same
  // keys go to the search page instead, so the shortcut has one meaning at
  // every width. No editor keymap uses Mod-K, so nothing is stolen.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey) return
      if (event.key.toLowerCase() !== 'k') return
      event.preventDefault()
      const input = searchRef.current
      if (input) {
        input.focus()
        input.select()
      } else {
        navigate('/search')
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [navigate])

  const handleSearchSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const query = new FormData(e.currentTarget).get('q')
    if (typeof query === 'string' && query.trim().length > 0) {
      navigate(`/search?q=${encodeURIComponent(query.trim())}`)
    }
  }

  const themeToggleLabel = mode === 'light' ? 'Switch to dark mode' : 'Switch to light mode'
  const closeOverflow = () => setOverflowAnchor(null)
  const closeAccount = () => setAccountAnchor(null)

  return (
    <Box
      component="header"
      sx={{
        display: 'flex',
        alignItems: 'center',
        gap: 1,
        height: APP_BAR_HEIGHT,
        flexShrink: 0,
        px: { xs: 1, sm: 2 },
        bgcolor: 'background.paper',
        borderBottom: '1px solid',
        borderColor: 'divider',
        zIndex: (t) => t.zIndex.appBar,
        ...NAVBAR_BUTTONS_SX,
        ...PRINT_HIDDEN,
      }}
    >
      <Tooltip title={navOpen ? 'Collapse navigation' : 'Expand navigation'}>
        <IconButton
          aria-label={navOpen ? 'Collapse navigation' : 'Expand navigation'}
          aria-expanded={navOpen}
          onClick={onToggleNav}
        >
          <MenuIcon />
        </IconButton>
      </Tooltip>

      {/* The brand, flat rather than a gradient: a gradient is a background
          image, and the discipline that keeps Paper flat
          (theme/componentCustomizations.ts) is worth holding to even where no
          text sits on top. */}
      <Box
        component={RouterLink}
        to="/"
        aria-label="RocketWiki home"
        sx={{
          display: 'flex',
          alignItems: 'center',
          gap: 1,
          px: 0.5,
          mr: { xs: 0, sm: 1 },
          borderRadius: 1,
          textDecoration: 'none',
          color: 'inherit',
          flexShrink: 0,
          '&:hover': { bgcolor: 'action.hover' },
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

      {/* The search box takes the middle of the bar — a docs site's one
          always-present input — and gives way to a plain icon on a phone. */}
      <Box sx={{ flexGrow: 1, display: 'flex', justifyContent: 'center', minWidth: 0 }}>
        {!isCompact && (
          <Box component="form" onSubmit={handleSearchSubmit} sx={{ width: '100%', maxWidth: 400 }}>
            <TextField
              name="q"
              size="small"
              fullWidth
              placeholder="Search…"
              // Seeded from the URL, and re-seeded when it changes: on
              // `/search?q=igniter` this box sat empty directly above the
              // page's own Query field showing "igniter", so the two boxes
              // disagreed about what had been searched for. Uncontrolled with a
              // `key`, not controlled — the header must not re-render on every
              // keystroke, and `defaultValue` alone would never pick up a
              // Back/Forward.
              key={currentQuery}
              defaultValue={currentQuery}
              inputRef={searchRef}
              slotProps={{
                input: {
                  startAdornment: (
                    <InputAdornment position="start">
                      <SearchIcon fontSize="small" />
                    </InputAdornment>
                  ),
                  endAdornment: (
                    <InputAdornment position="end" sx={{ display: { xs: 'none', md: 'flex' } }}>
                      <Kbd>{`${modifierKeyLabel()} K`}</Kbd>
                    </InputAdornment>
                  ),
                },
                // On the <input> itself, not the TextField: a root-level
                // aria-label lands on the FormControl div, where the generic
                // role prohibits it (axe aria-prohibited-attr, WCAG 4.1.2).
                htmlInput: { 'aria-label': 'Search the wiki', 'aria-keyshortcuts': 'Control+K Meta+K' },
              }}
            />
          </Box>
        )}
      </Box>

      {/* Ask, notifications, theme, help, admin, account. */}
      <Stack direction="row" sx={{ gap: 0.5, alignItems: 'center', flexShrink: 0 }}>
        {isCompact && (
          <Tooltip title="Search">
            <IconButton component={RouterLink} to="/search" aria-label="Search the wiki">
              <SearchIcon />
            </IconButton>
          </Tooltip>
        )}

        {/* Next to search: the two "find something" affordances live together.
            Self-hiding once the session learns the assistant is
            NOT_CONFIGURED (ask/askAvailability.ts). */}
        <AskWikiEntryButton />

        <NotificationBell />

        {isCompact ? (
          <>
            <Tooltip title="More">
              <IconButton
                aria-label="More actions"
                aria-haspopup="menu"
                onClick={(e) => setOverflowAnchor(e.currentTarget)}
              >
                <MoreVertIcon />
              </IconButton>
            </Tooltip>
            <Menu anchorEl={overflowAnchor} open={Boolean(overflowAnchor)} onClose={closeOverflow}>
              <MenuItem
                onClick={() => {
                  closeOverflow()
                  toggle()
                }}
              >
                <ListItemIcon>
                  {mode === 'light' ? <Brightness4Icon fontSize="small" /> : <Brightness7Icon fontSize="small" />}
                </ListItemIcon>
                {themeToggleLabel}
              </MenuItem>
              <MenuItem component={RouterLink} to="/-/docs" onClick={closeOverflow}>
                <ListItemIcon>
                  <HelpOutlineOutlinedIcon fontSize="small" />
                </ListItemIcon>
                Help
              </MenuItem>
              {isInstanceAdmin && (
                <MenuItem component={RouterLink} to="/admin" onClick={closeOverflow}>
                  <ListItemIcon>
                    <AdminPanelSettingsOutlinedIcon fontSize="small" />
                  </ListItemIcon>
                  Admin
                </MenuItem>
              )}
            </Menu>
          </>
        ) : (
          <>
            <Tooltip title={themeToggleLabel}>
              <IconButton onClick={toggle} aria-label="Toggle color mode">
                {mode === 'light' ? <Brightness4Icon /> : <Brightness7Icon />}
              </IconButton>
            </Tooltip>

            {/* Help sits with the other always-available actions rather than in a
                menu: the reader who needs it least knows where to look for it. */}
            <Tooltip title="Help">
              <IconButton component={RouterLink} to="/-/docs" aria-label="Help">
                <HelpOutlineOutlinedIcon />
              </IconButton>
            </Tooltip>

            {isInstanceAdmin && (
              <Tooltip title="Admin">
                <IconButton component={RouterLink} to="/admin" aria-label="Admin">
                  <AdminPanelSettingsOutlinedIcon />
                </IconButton>
              </Tooltip>
            )}
          </>
        )}

        {/* The account menu, behind the avatar at the bar's end — where a
            person's own things live: profile, settings, sign out. */}
        <Tooltip title={displayName}>
          <IconButton
            aria-label="Account menu"
            aria-haspopup="menu"
            onClick={(e) => setAccountAnchor(e.currentTarget)}
            sx={{ ml: 0.5 }}
          >
            <UserAvatar
              userId={meData?.me.localUserId}
              hasAvatar={meData?.me.hasAvatar}
              displayName={displayName}
              size={28}
            />
          </IconButton>
        </Tooltip>
        <Menu
          anchorEl={accountAnchor}
          open={Boolean(accountAnchor)}
          onClose={closeAccount}
          anchorOrigin={{ horizontal: 'right', vertical: 'bottom' }}
          transformOrigin={{ horizontal: 'right', vertical: 'top' }}
          slotProps={{ paper: { sx: { minWidth: 220 } } }}
        >
          {/* Who this menu is about. Not an item: it is where the rail's
              identity block went, and it is information, not an action. */}
          <Box sx={{ px: 1.5, pt: 0.5, pb: 1, minWidth: 0 }}>
            <Typography variant="body2" noWrap sx={{ fontWeight: 600 }}>
              {displayName}
            </Typography>
            <Typography variant="caption" noWrap sx={{ color: 'text.secondary', display: 'block' }}>
              {email ?? 'Not signed in'}
            </Typography>
          </Box>
          <Divider sx={{ mb: 0.5 }} />
          {/* Hidden, not disabled, until `me.localUserId` has arrived: the
              profile route takes the local id, and an item that navigated to
              /people/undefined would be a menu entry that leads to a 404. A
              user's own id is not a secret from them, so this is not the
              absent-rather-than-forbidden question — just a link that cannot
              be written yet. */}
          {meData?.me.localUserId && (
            <MenuItem component={RouterLink} to={profilePath(meData.me.localUserId)} onClick={closeAccount}>
              <ListItemIcon>
                <AccountCircleOutlinedIcon fontSize="small" />
              </ListItemIcon>
              My profile
            </MenuItem>
          )}
          <MenuItem
            onClick={() => {
              closeAccount()
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
              closeAccount()
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
    </Box>
  )
}
