import { alpha, type PaletteOptions } from '@mui/material/styles'

/**
 * Colour ramps taken verbatim from MUI's Dashboard template
 * (`docs/data/material/getting-started/templates/shared-theme/themePrimitives.ts`,
 * v9.4.0 — the version this repo runs). Most of that template's look is these
 * ramps plus the surface treatment in `componentCustomizations.ts`, so they are
 * copied rather than re-derived: a hand-picked near-miss would drift from the
 * reference every time it was touched.
 *
 * Vendored template source is code no dependency bot can patch, so the copied
 * surface stops here — the ramps and the component overrides that carry the
 * look. The template's dashboard furniture (KPI cards, charts, its own shell
 * components) is not in this repo and its four `@mui/x-*` dependencies are not
 * installed.
 */
export const brand = {
  50: 'hsl(210, 100%, 95%)',
  100: 'hsl(210, 100%, 92%)',
  200: 'hsl(210, 100%, 80%)',
  300: 'hsl(210, 100%, 65%)',
  400: 'hsl(210, 98%, 48%)',
  500: 'hsl(210, 98%, 42%)',
  600: 'hsl(210, 98%, 55%)',
  700: 'hsl(210, 100%, 35%)',
  800: 'hsl(210, 100%, 16%)',
  900: 'hsl(210, 100%, 21%)',
}

export const gray = {
  50: 'hsl(220, 35%, 97%)',
  100: 'hsl(220, 30%, 94%)',
  200: 'hsl(220, 20%, 88%)',
  300: 'hsl(220, 20%, 80%)',
  400: 'hsl(220, 20%, 65%)',
  500: 'hsl(220, 20%, 42%)',
  600: 'hsl(220, 20%, 35%)',
  700: 'hsl(220, 20%, 25%)',
  800: 'hsl(220, 30%, 6%)',
  900: 'hsl(220, 35%, 3%)',
}

export const green = {
  50: 'hsl(120, 80%, 98%)',
  100: 'hsl(120, 75%, 94%)',
  200: 'hsl(120, 75%, 87%)',
  300: 'hsl(120, 61%, 77%)',
  400: 'hsl(120, 44%, 53%)',
  500: 'hsl(120, 59%, 30%)',
  600: 'hsl(120, 70%, 25%)',
  700: 'hsl(120, 75%, 16%)',
  800: 'hsl(120, 84%, 10%)',
  900: 'hsl(120, 87%, 6%)',
}

export const red = {
  50: 'hsl(0, 100%, 97%)',
  100: 'hsl(0, 92%, 90%)',
  200: 'hsl(0, 94%, 80%)',
  300: 'hsl(0, 90%, 65%)',
  400: 'hsl(0, 90%, 40%)',
  500: 'hsl(0, 90%, 30%)',
  600: 'hsl(0, 91%, 25%)',
  700: 'hsl(0, 94%, 18%)',
  800: 'hsl(0, 95%, 12%)',
  900: 'hsl(0, 93%, 6%)',
}

/**
 * `default` is the content region and `paper` is every surface on it: the
 * template's arrangement is a near-white page carrying white bordered cards,
 * with the side-menu rail one shade off (`gray[50]`, applied to the drawer in
 * `componentCustomizations.ts`). Dark mode keeps the same relationship with
 * the page as the darkest surface.
 *
 * `action.hover` / `action.selected` are stronger than the template's. Theirs
 * resolve to roughly `#fdfdfe` and `#fafbfc` over white — the template's own
 * components rarely show them because buttons, tabs and menu items each define
 * an explicit hover, but this app's navigation leans on `Mui-selected`, and
 * "which page am I on" is persistent information rather than a transient hint.
 */
export const lightPalette: PaletteOptions = {
  mode: 'light',
  primary: {
    // brand[500] rather than the template's brand[400]: contained buttons here
    // are the template's neutral charcoal, which leaves primary carrying text
    // roles (links, text buttons, `color="primary"` icons). brand[400] is
    // 4.14:1 on white — below WCAG 1.4.3 — where brand[500] is 5.18:1 on white
    // and 4.79:1 on the page background.
    main: brand[500],
    contrastText: '#ffffff',
  },
  // MUI's default light warning (#ed6c02) fails WCAG 1.4.3 both as text on
  // light backgrounds (2.9:1) and as a contained-button background under white
  // text (3.1:1) — the browser a11y layer (web/a11y) flagged the "Move
  // anyway"/"Overwrite anyway"/"Archive" affordances. #b45309 keeps the amber
  // intent at >=4.5:1 in both roles (4.7:1 on the page background, 5.0:1 under
  // white text). The template's own orange[400] is 2.8:1 under white text, so
  // adopting its warning ramp would reintroduce exactly the flagged failure.
  warning: { main: '#b45309' },
  divider: alpha(gray[300], 0.4),
  background: {
    default: 'hsl(0, 0%, 99%)',
    paper: '#ffffff',
  },
  text: {
    primary: gray[800],
    secondary: gray[600],
  },
  action: {
    hover: alpha(gray[300], 0.35),
    selected: alpha(gray[300], 0.6),
  },
}

export const darkPalette: PaletteOptions = {
  mode: 'dark',
  // The template's dark primary is brand[400], which is 4.62:1 on the dark
  // paper — passing, but with nothing left for a surface that sits a shade
  // lighter. brand[300] (the template's own dark `primary.light`) is 7.45:1.
  primary: { main: brand[300] },
  // Default dark error (#f44336) with MUI's auto-picked WHITE contrast text is
  // 3.7:1 (contained "Delete") — below WCAG 1.4.3. Black text on the same red
  // is 5.7:1, and #f44336 itself stays (it still clears 4.5:1 as error TEXT on
  // the dark backgrounds).
  error: { main: '#f44336', contrastText: '#000000' },
  divider: alpha(gray[700], 0.6),
  background: {
    default: gray[900],
    paper: 'hsl(220, 30%, 7%)',
  },
  text: {
    primary: 'hsl(0, 0%, 100%)',
    secondary: gray[400],
  },
  action: {
    hover: alpha(gray[600], 0.25),
    selected: alpha(gray[600], 0.45),
  },
}
