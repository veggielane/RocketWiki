import { useState } from 'react'
import { Outlet, useNavigation } from 'react-router-dom'
import { Box, LinearProgress, Stack } from '@mui/material'
import { CLASSIFICATION_BANNER_HEIGHT } from '../markings/ClassificationBanner'
import { useEmojiRegistryFeed } from '../emoji/useEmojiRegistry'
import { AppHeader } from './AppHeader'
import { SideMenu } from './SideMenu'

/**
 * The reading measure for everything in the content region. The template's
 * dashboard runs nearly full-bleed, which suits a grid of cards and not a wiki:
 * prose at 1150px is a 150-character line. The header strip shares the value so
 * the two read as one column rather than as a full-width bar over a narrow one.
 */
const CONTENT_MAX_WIDTH = 960

/**
 * The app's frame, following the MUI Dashboard template's layout: a navigation
 * rail down the left, and a content region whose own header strip carries the
 * breadcrumb and the global actions. There is no top app bar — the template has
 * none at desktop widths, and the things one would hold (identity, account,
 * search, notifications) are in the rail and the header strip instead.
 */
export function AppShell() {
  const [navOpen, setNavOpen] = useState(true)
  // Routes load their page component lazily (router.tsx) so the initial bundle
  // isn't paying for every route up front — this is the visible trade-off for
  // that: a route whose chunk isn't cached yet has a brief gap between click
  // and render. Surfacing it beats a navigation that appears to silently do
  // nothing.
  const navigation = useNavigation()
  // One feed for the custom-emoji registry (emoji/registry.ts) — every
  // editor/picker below the shell reads the module store.
  useEmojiRegistryFeed()

  return (
    <Box sx={{ display: 'flex', height: '100vh' }}>
      {/* Keyboard-only until focused — lets a keyboard/screen-reader user
          skip the rail and header's many tab stops on every single
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

      <SideMenu open={navOpen} />

      <Box
        component="main"
        id="main-content"
        tabIndex={-1}
        sx={{
          flexGrow: 1,
          minWidth: 0,
          overflow: 'auto',
          bgcolor: 'background.default',
          outline: 'none',
          // WCAG 2.4.11 (focus not obscured): the classification banner is
          // position:fixed over the bottom of this scroll container, so when
          // the browser scrolls a focused element into view it could land
          // underneath it. The header strip scrolls with the content and needs
          // no equivalent at the top, which the old fixed app bar did.
          scrollPaddingBottom: `${CLASSIFICATION_BANNER_HEIGHT + 8}px`,
        }}
      >
        {navigation.state !== 'idle' && (
          <LinearProgress sx={{ position: 'sticky', top: 0, zIndex: 1 }} aria-label="Loading page" />
        )}
        <Stack
          spacing={2}
          sx={{
            alignItems: 'center',
            mx: 3,
            // Bottom padding reserves the fixed classification banner's strip so
            // a page's last line is never hidden underneath it. Applied here
            // rather than per-route because the banner is viewport-fixed: it
            // overlaps whatever is scrolled to the bottom, marked page or not.
            pb: `${CLASSIFICATION_BANNER_HEIGHT + 32}px`,
          }}
        >
          <Box sx={{ width: '100%', maxWidth: CONTENT_MAX_WIDTH }}>
            <AppHeader navOpen={navOpen} onToggleNav={() => setNavOpen((v) => !v)} />
          </Box>
          <Box sx={{ width: '100%', maxWidth: CONTENT_MAX_WIDTH }}>
            <Outlet />
          </Box>
        </Stack>
      </Box>
    </Box>
  )
}
