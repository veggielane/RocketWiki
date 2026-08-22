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
  },
}

export const lightTheme = createTheme({
  ...baseOptions,
  palette: {
    mode: 'light',
    primary: { main: '#1a56db' },
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
    background: {
      default: '#0f1115',
      paper: '#171a21',
    },
  },
})

export type ThemeMode = 'light' | 'dark'
