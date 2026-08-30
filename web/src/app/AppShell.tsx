import { useCallback, useEffect, useRef, useState } from 'react'
import { Outlet, useLocation, useNavigation } from 'react-router-dom'
import { Box, LinearProgress, Stack, useMediaQuery, useTheme } from '@mui/material'
import { visuallyHidden } from '@mui/utils'
import { CLASSIFICATION_BANNER_HEIGHT } from '../markings/ClassificationBanner'
import { useEmojiRegistryFeed } from '../emoji/useEmojiRegistry'
import { AppHeader } from './AppHeader'
import { SideMenu } from './SideMenu'
import { PageTitleContext, composeDocumentTitle } from './documentTitle'
import { routeTitleFor } from './routeCrumbs'
import { useCanonicalSpace } from './useCanonicalSpaceKey'
import { measureFor } from './contentMeasure'


/**
 * The app's frame, following the MUI Dashboard template's layout: a navigation
 * rail down the left, and a content region whose own header strip carries the
 * breadcrumb and the global actions. There is no top app bar — the template has
 * none at desktop widths, and the things one would hold (identity, account,
 * search, notifications) are in the rail and the header strip instead.
 */
export function AppShell() {
  const theme = useTheme()
  // The rail is 240px of a 375px phone. `persistent` at every width left about
  // 87px of content, so below `md` it becomes an overlay that starts closed —
  // and the desktop open/closed choice is remembered, which it never was.
  const isCompact = useMediaQuery(theme.breakpoints.down('md'))
  const [navOpen, setNavOpen] = useState(() => !isCompact && readStoredNavOpen())
  const { pathname } = useLocation()
  // Routes load their page component lazily (router.tsx) so the initial bundle
  // isn't paying for every route up front — this is the visible trade-off for
  // that: a route whose chunk isn't cached yet has a brief gap between click
  // and render. Surfacing it beats a navigation that appears to silently do
  // nothing.
  const navigation = useNavigation()
  // One feed for the custom-emoji registry (emoji/registry.ts) — every
  // editor/picker below the shell reads the module store.
  useEmojiRegistryFeed()

  const mainRef = useRef<HTMLElement>(null)
  // The subject a screen registered for itself (documentTitle.ts). The shell is
  // the only writer of `document.title`; screens only supply the noun.
  const [pageTitle, setPageTitle] = useState<string | null>(null)
  // Identity-stable so `useDocumentTitle`'s effect does not re-fire every render.
  const registerPageTitle = useCallback((title: string | null) => setPageTitle(title), [])

  // The server's spelling of the space key, so a tab opened at `/spaces/eng`
  // is titled the same as one opened at `/spaces/ENG`. Same hook the breadcrumb
  // uses, over queries the rail already runs — urql serves both from cache.
  //
  // Called on its own line, not inline in the `??` below: the right-hand side of
  // `??` is skipped when the left is non-null, so a screen that had registered
  // its own title would silently stop calling the hook and change the hook order
  // between renders.
  const canonicalSpace = useCanonicalSpace(pathname)
  const title = pageTitle ?? routeTitleFor(pathname, canonicalSpace)

  useEffect(() => {
    document.title = composeDocumentTitle(title)
  }, [title])

  // Crossing the breakpoint closes the overlay rather than leaving a 240px
  // drawer sitting over a phone-width screen. Adjusted during render against a
  // tracked copy rather than in an effect — the repo's idiom for "react to a
  // changed input" (SpaceTreeNav's path key, PageViewPage's page id), and it
  // avoids the extra render an effect-then-setState pass costs.
  const [trackedCompact, setTrackedCompact] = useState(isCompact)
  if (trackedCompact !== isCompact) {
    setTrackedCompact(isCompact)
    setNavOpen(isCompact ? false : readStoredNavOpen())
  }

  // Focus moves to the content region on every navigation, which is the whole
  // reason `#main-content` is `tabIndex={-1}`. Without it a client-side route
  // change leaves focus on the link that was activated, the accessible page name
  // never changes, and a screen-reader user is told nothing at all happened
  // (WCAG 2.4.3, and 2.4.2 via the title above). Skipped on first render: the
  // browser's own initial focus is correct, and stealing it would fight the
  // skip link.
  const firstRender = useRef(true)
  useEffect(() => {
    if (firstRender.current) {
      firstRender.current = false
      return
    }
    mainRef.current?.focus()
  }, [pathname])

  const toggleNav = () => {
    setNavOpen((open) => {
      const next = !open
      if (!isCompact) storeNavOpen(next)
      return next
    })
  }

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
          zIndex: (t) => t.zIndex.tooltip + 1,
          p: 1.5,
          bgcolor: 'background.paper',
          color: 'text.primary',
          '&:focus': { left: 8, top: 8, position: 'fixed' },
        }}
      >
        Skip to main content
      </Box>

      <SideMenu open={navOpen} temporary={isCompact} onClose={() => setNavOpen(false)} />

      <Box
        component="main"
        id="main-content"
        ref={mainRef}
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
        {/* Focusing `<main>` moves the reading cursor but announces nothing on
            its own. This is what says where you landed — the same
            visually-hidden status region the ask page uses for its answers. */}
        <Box role="status" aria-live="polite" sx={visuallyHidden}>
          {title}
        </Box>
        <Stack
          spacing={2}
          sx={{
            alignItems: 'center',
            mx: { xs: 2, sm: 3 },
            // Bottom padding reserves the fixed classification banner's strip so
            // a page's last line is never hidden underneath it. Applied here
            // rather than per-route because the banner is viewport-fixed: it
            // overlaps whatever is scrolled to the bottom, marked page or not.
            pb: `${CLASSIFICATION_BANNER_HEIGHT + 32}px`,
          }}
        >
          <Box sx={{ width: '100%', maxWidth: measureFor(pathname) }}>
            <AppHeader navOpen={navOpen} onToggleNav={toggleNav} />
          </Box>
          <Box sx={{ width: '100%', maxWidth: measureFor(pathname) }}>
            <PageTitleContext value={registerPageTitle}>
              <Outlet />
            </PageTitleContext>
          </Box>
        </Stack>
      </Box>
    </Box>
  )
}

const NAV_OPEN_KEY = 'rocketwiki:nav-open'

function readStoredNavOpen(): boolean {
  try {
    return window.localStorage.getItem(NAV_OPEN_KEY) !== 'false'
  } catch {
    // Storage can throw outright (private modes, blocked third-party contexts).
    // The rail being open is the better default, so failure reads as "open".
    return true
  }
}

function storeNavOpen(open: boolean): void {
  try {
    window.localStorage.setItem(NAV_OPEN_KEY, String(open))
  } catch {
    // Remembering the rail is a nicety; failing to is not worth an error.
  }
}
