import { createTheme, type Shadows, type ThemeOptions } from '@mui/material/styles'
import { componentCustomizations, softShadow } from './componentCustomizations'
import { darkPalette, lightPalette } from './palette'

const defaultTheme = createTheme()

/**
 * Shadow level 1 replaced by the template's soft, wide `baseShadow`; the rest
 * of MUI's stack is left alone. Level 1 is what a default `Paper` and most
 * floating surfaces land on, so it is the one that shows.
 */
const shadowsFor = (mode: 'light' | 'dark'): Shadows => {
  const shadows = [...defaultTheme.shadows] as Shadows
  shadows[1] = softShadow[mode]
  return shadows
}

/**
 * Shared design tokens for both palettes, following MUI's Dashboard template
 * (v9.4.0): a 14px body, a compressed heading scale and an 8px radius, which
 * together give the denser, flatter surface a documentation tool wants.
 *
 * The template asks for Inter and nothing more. No webfont is bundled here —
 * an air-gapped deployment cannot fetch one and adding it as an asset is a
 * separate decision — so the existing stack stays and Inter is used only where
 * it is already installed. On a stock Windows or macOS client this renders in
 * Segoe UI or San Francisco, which is a visible difference from the template's
 * screenshots and the one part of its typography that is not adopted.
 */
const baseOptions: ThemeOptions = {
  shape: {
    borderRadius: 8,
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
    h1: { fontSize: defaultTheme.typography.pxToRem(48), fontWeight: 600, lineHeight: 1.2, letterSpacing: -0.5 },
    h2: { fontSize: defaultTheme.typography.pxToRem(36), fontWeight: 600, lineHeight: 1.2 },
    h3: { fontSize: defaultTheme.typography.pxToRem(30), lineHeight: 1.2 },
    h4: { fontSize: defaultTheme.typography.pxToRem(24), fontWeight: 600, lineHeight: 1.5 },
    h5: { fontSize: defaultTheme.typography.pxToRem(20), fontWeight: 600 },
    h6: { fontSize: defaultTheme.typography.pxToRem(18), fontWeight: 600 },
    subtitle1: { fontSize: defaultTheme.typography.pxToRem(18) },
    subtitle2: { fontSize: defaultTheme.typography.pxToRem(14), fontWeight: 500 },
    body1: { fontSize: defaultTheme.typography.pxToRem(14) },
    body2: { fontSize: defaultTheme.typography.pxToRem(14), fontWeight: 400 },
    caption: { fontSize: defaultTheme.typography.pxToRem(12), fontWeight: 400 },
  },
  components: componentCustomizations,
}

/**
 * Two themes rather than the template's single `cssVariables` theme with a
 * `colorSchemes` pair. The app's toggle stamps `data-theme` on the root
 * element, which the plain-CSS editor layer (editor-content.css) and the a11y
 * capture harness both key on; switching to MUI's colour-scheme selector would
 * put the palette behind a different attribute and quietly leave the dark
 * captures rendering light.
 */
export const lightTheme = createTheme({
  ...baseOptions,
  palette: lightPalette,
  shadows: shadowsFor('light'),
})

export const darkTheme = createTheme({
  ...baseOptions,
  palette: darkPalette,
  shadows: shadowsFor('dark'),
})

export type ThemeMode = 'light' | 'dark'
