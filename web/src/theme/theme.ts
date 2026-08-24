import { createTheme, type ThemeOptions } from '@mui/material/styles'

/**
 * Shared design tokens for both palettes. RocketWiki has no brand palette
 * yet, so this leans on MUI defaults with a slightly denser layout suited
 * to a documentation tool (lots of tree navigation + text).
 */
const baseOptions: ThemeOptions = {
  shape: {
    borderRadius: 6,
  },
  typography: {
    fontFamily: [
      'Inter',
      '-apple-system',
      'BlinkMacSystemFont',
      '"Segoe UI"',
      'Roboto',
      'Helvetica',
      'Arial',
      'sans-serif',
    ].join(','),
  },
  components: {
    MuiButton: {
      defaultProps: {
        disableElevation: true,
      },
    },
    MuiAppBar: {
      defaultProps: {
        elevation: 0,
      },
    },
    // Flat surfaces in dark mode: MUI's dark elevation overlay is a
    // background-image gradient, which (a) doesn't fit the app's flat look
    // (AppBar/buttons already disable elevation) and (b) makes text on any
    // elevated Paper unverifiable by contrast tooling — axe abstains on
    // gradient backgrounds, so the app bar and drawer would silently drop
    // out of the automated 1.4.3 checks (docs/ACCESSIBILITY.md).
    MuiPaper: {
      styleOverrides: {
        root: {
          backgroundImage: 'none',
        },
      },
    },
  },
}

export const lightTheme = createTheme({
  ...baseOptions,
  palette: {
    mode: 'light',
    primary: { main: '#1a56db' },
    // MUI's default light warning (#ed6c02) fails WCAG 1.4.3 both as text on
    // light backgrounds (2.9:1) and as a contained-button background under
    // white text (3.1:1) — the browser a11y layer (web/a11y) flagged the
    // "Move anyway"/"Overwrite anyway"/"Archive" affordances. #b45309 keeps
    // the amber intent at ≥4.5:1 in both roles (4.7:1 on #f7f8fa, 5.0:1
    // under white text).
    warning: { main: '#b45309' },
    background: {
      default: '#f7f8fa',
      paper: '#ffffff',
    },
  },
})

export const darkTheme = createTheme({
  ...baseOptions,
  palette: {
    mode: 'dark',
    primary: { main: '#6b9bf7' },
    // Default dark error (#f44336) with MUI's auto-picked WHITE contrast
    // text is 3.7:1 (the filled DENIED chip in the audit log) — below WCAG
    // 1.4.3. Black text on the same red is 5.7:1, and #f44336 itself stays
    // (it still clears 4.5:1 as error TEXT on the dark backgrounds).
    error: { main: '#f44336', contrastText: '#000000' },
    background: {
      default: '#0f1115',
      paper: '#171a21',
    },
  },
})

export type ThemeMode = 'light' | 'dark'
