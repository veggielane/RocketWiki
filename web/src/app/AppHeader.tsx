import { useState } from 'react'
import {
  Box,
  IconButton,
  InputAdornment,
  ListItemIcon,
  Menu,
  MenuItem,
  Stack,
  TextField,
  Tooltip,
  useMediaQuery,
  useTheme,
} from '@mui/material'
import AdminPanelSettingsOutlinedIcon from '@mui/icons-material/AdminPanelSettingsOutlined'
import HelpOutlineOutlinedIcon from '@mui/icons-material/HelpOutlineOutlined'
import Brightness4Icon from '@mui/icons-material/Brightness4'
import Brightness7Icon from '@mui/icons-material/Brightness7'
import MenuIcon from '@mui/icons-material/Menu'
import MoreVertIcon from '@mui/icons-material/MoreVert'
import SearchIcon from '@mui/icons-material/Search'
import { Link as RouterLink, useLocation, useNavigate, useSearchParams } from 'react-router-dom'
import { AskWikiEntryButton } from '../ask/AskWikiEntryButton'
import { NotificationBell } from '../notifications/NotificationBell'
import { useIsInstanceAdmin } from '../auth/useIsInstanceAdmin'
import { useColorMode } from '../theme/colorModeContext'
import { AppBreadcrumbs } from './AppBreadcrumbs'

/**
 * The Dashboard template's header buttons are bordered, filled squares rather
 * than bare glyphs. The treatment is applied here rather than to
 * `MuiIconButton` in the theme because the template only ever has icon buttons
 * in its header: globally it would also box every tree chevron and every
 * remove-row button in a form, and the tree's chevrons are sized to WCAG
 * 2.5.8's floor exactly (SpaceTreeNav.tsx), which a border would not respect.
 */
const HEADER_BUTTONS_SX = {
  '& .MuiIconButton-root': {
    width: 36,
    height: 36,
    borderRadius: 1,
    border: '1px solid',
    borderColor: 'divider',
    bgcolor: 'background.paper',
    color: 'text.primary',
  },
} as const

export interface AppHeaderProps {
  navOpen: boolean
  onToggleNav: () => void
}

/**
 * The strip above the content: where you are on the left, what you can reach
 * from anywhere on the right. It scrolls with the content rather than sitting
 * fixed, which is the template's behaviour and is why nothing here needs the
 * `scroll-padding` a fixed bar would (WCAG 2.4.11).
 *
 * The nav toggle is the one control the template's header has no equivalent
 * of; the rail is `permanent` there and collapsing it is an affordance this
 * app already had.
 *
 * Below `sm` the search field and the low-frequency actions (theme, help,
 * admin) collapse into an overflow menu. The cluster is six controls plus a
 * 25ch field in a `flexShrink: 0` row, which needs roughly 350px it does not
 * have on a phone — and the breadcrumb has to fit beside it.
 */
export function AppHeader({ navOpen, onToggleNav }: AppHeaderProps) {
  const navigate = useNavigate()
  const { pathname } = useLocation()
  const theme = useTheme()
  const isCompact = useMediaQuery(theme.breakpoints.down('sm'))
  const { mode, toggle } = useColorMode()
  const [overflowAnchor, setOverflowAnchor] = useState<HTMLElement | null>(null)
  // The admin surfaces are instance-admin-only (router.tsx), and an ungated
  // button here sent everyone else to a 404. Hidden rather than disabled, which
  // is this app's posture for an absent capability everywhere else — the Ask
  // button beside it returns null the same way when the assistant is not
  // configured. A user's own role is not a secret from them, so this is not the
  // §6.7 read-path question.
  const { isInstanceAdmin } = useIsInstanceAdmin()

  // What the search page is currently showing, when that is where we are.
  const [searchParams] = useSearchParams()
  const currentQuery = pathname === '/search' ? (searchParams.get('q') ?? '') : ''

  const handleSearchSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const query = new FormData(e.currentTarget).get('q')
    if (typeof query === 'string' && query.trim().length > 0) {
      navigate(`/search?q=${encodeURIComponent(query.trim())}`)
    }
  }

  const themeToggleLabel = mode === 'light' ? 'Switch to dark mode' : 'Switch to light mode'

  return (
    <Stack
      direction="row"
      spacing={2}
      sx={{ width: '100%', alignItems: 'center', justifyContent: 'space-between', pt: 1.5, ...HEADER_BUTTONS_SX }}
    >
      <Stack direction="row" sx={{ alignItems: 'center', gap: 1, minWidth: 0 }}>
        <Tooltip title={navOpen ? 'Collapse navigation' : 'Expand navigation'}>
          <IconButton
            aria-label={navOpen ? 'Collapse navigation' : 'Expand navigation'}
            aria-expanded={navOpen}
            onClick={onToggleNav}
          >
            <MenuIcon />
          </IconButton>
        </Tooltip>
        <AppBreadcrumbs />
      </Stack>

      <Stack direction="row" sx={{ gap: 1, alignItems: 'center', flexShrink: 0 }}>
        {isCompact ? (
          <Tooltip title="Search">
            <IconButton component={RouterLink} to="/search" aria-label="Search the wiki">
              <SearchIcon />
            </IconButton>
          </Tooltip>
        ) : (
          <Box component="form" onSubmit={handleSearchSubmit}>
            <TextField
              name="q"
              size="small"
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
              sx={{ width: '25ch' }}
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
        )}

        {/* Next to search: the two "find something" affordances live together.
            Self-hiding once the session learns the assistant is
            NOT_CONFIGURED (ask/askAvailability.ts). */}
        <AskWikiEntryButton />

        {/* Never collapsed: an unread count you have to open a menu to see is
            not a notification. */}
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
            <Menu
              anchorEl={overflowAnchor}
              open={Boolean(overflowAnchor)}
              onClose={() => setOverflowAnchor(null)}
            >
              <MenuItem
                onClick={() => {
                  setOverflowAnchor(null)
                  toggle()
                }}
              >
                <ListItemIcon>
                  {mode === 'light' ? <Brightness4Icon fontSize="small" /> : <Brightness7Icon fontSize="small" />}
                </ListItemIcon>
                {themeToggleLabel}
              </MenuItem>
              <MenuItem component={RouterLink} to="/-/docs" onClick={() => setOverflowAnchor(null)}>
                <ListItemIcon>
                  <HelpOutlineOutlinedIcon fontSize="small" />
                </ListItemIcon>
                Help
              </MenuItem>
              {isInstanceAdmin && (
                <MenuItem component={RouterLink} to="/admin" onClick={() => setOverflowAnchor(null)}>
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
      </Stack>
    </Stack>
  )
}
