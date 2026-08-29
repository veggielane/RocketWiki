import { Box, IconButton, InputAdornment, Stack, TextField, Tooltip } from '@mui/material'
import AdminPanelSettingsOutlinedIcon from '@mui/icons-material/AdminPanelSettingsOutlined'
import Brightness4Icon from '@mui/icons-material/Brightness4'
import Brightness7Icon from '@mui/icons-material/Brightness7'
import MenuIcon from '@mui/icons-material/Menu'
import SearchIcon from '@mui/icons-material/Search'
import { Link as RouterLink, useNavigate } from 'react-router-dom'
import { AskWikiEntryButton } from '../ask/AskWikiEntryButton'
import { NotificationBell } from '../notifications/NotificationBell'
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
 */
export function AppHeader({ navOpen, onToggleNav }: AppHeaderProps) {
  const navigate = useNavigate()
  const { mode, toggle } = useColorMode()

  const handleSearchSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const query = new FormData(e.currentTarget).get('q')
    if (typeof query === 'string' && query.trim().length > 0) {
      navigate(`/search?q=${encodeURIComponent(query.trim())}`)
    }
  }

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
        <Box component="form" onSubmit={handleSearchSubmit}>
          <TextField
            name="q"
            size="small"
            placeholder="Search…"
            sx={{ width: { xs: '100%', md: '25ch' } }}
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

        {/* Next to search: the two "find something" affordances live together.
            Self-hiding once the session learns the assistant is
            NOT_CONFIGURED (ask/askAvailability.ts). */}
        <AskWikiEntryButton />

        <NotificationBell />

        <Tooltip title={mode === 'light' ? 'Switch to dark mode' : 'Switch to light mode'}>
          <IconButton onClick={toggle} aria-label="Toggle color mode">
            {mode === 'light' ? <Brightness4Icon /> : <Brightness7Icon />}
          </IconButton>
        </Tooltip>

        <Tooltip title="Admin">
          <IconButton component={RouterLink} to="/admin" aria-label="Admin">
            <AdminPanelSettingsOutlinedIcon />
          </IconButton>
        </Tooltip>
      </Stack>
    </Stack>
  )
}
