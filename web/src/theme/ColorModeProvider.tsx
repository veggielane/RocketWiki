import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { CssBaseline, ThemeProvider } from '@mui/material'
import { darkTheme, lightTheme, type ThemeMode } from './theme'
import { ColorModeContext, type ColorModeContextValue } from './colorModeContext'

const STORAGE_KEY = 'rocketwiki:color-mode'

function getInitialMode(): ThemeMode {
  const stored = window.localStorage.getItem(STORAGE_KEY)
  if (stored === 'light' || stored === 'dark') {
    return stored
  }
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

export function ColorModeProvider({ children }: { children: ReactNode }) {
  const [mode, setMode] = useState<ThemeMode>(getInitialMode)

  useEffect(() => {
    window.localStorage.setItem(STORAGE_KEY, mode)
    // The plain-CSS layer (editor-content.css) themes on
    // `:root[data-theme='dark']` in addition to prefers-color-scheme — the
    // app's own toggle must win over the OS preference there too, or the
    // editor content keeps light-theme colors (light code-block/table-header
    // backgrounds under white text) on an app-dark page. Found by the
    // browser a11y layer's dark captures (docs/ACCESSIBILITY.md).
    document.documentElement.dataset.theme = mode
  }, [mode])

  const value = useMemo<ColorModeContextValue>(
    () => ({
      mode,
      toggle: () => setMode((prev) => (prev === 'light' ? 'dark' : 'light')),
    }),
    [mode],
  )

  const theme = mode === 'light' ? lightTheme : darkTheme

  return (
    <ColorModeContext.Provider value={value}>
      <ThemeProvider theme={theme}>
        <CssBaseline />
        {children}
      </ThemeProvider>
    </ColorModeContext.Provider>
  )
}
