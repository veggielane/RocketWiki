import { alpha, type Components, type Theme } from '@mui/material/styles'
import { outlinedInputClasses } from '@mui/material/OutlinedInput'
import { listItemTextClasses } from '@mui/material/ListItemText'
import { chipClasses } from '@mui/material/Chip'
import { svgIconClasses } from '@mui/material/SvgIcon'
// Type-only, so nothing is pulled into the bundle: it teaches `Components<Theme>`
// about the `MuiDataGrid` key below.
import type {} from '@mui/x-data-grid/themeAugmentation'
import { gray, green, red } from './palette'

/**
 * The soft, wide drop shadow the Dashboard template uses for every floating
 * surface (its `baseShadow`), in place of MUI's tighter default stack.
 */
export const softShadow = {
  light: 'hsla(220, 30%, 5%, 0.07) 0px 4px 16px 0px, hsla(220, 25%, 10%, 0.07) 0px 8px 16px -5px',
  dark: 'hsla(220, 30%, 5%, 0.7) 0px 4px 16px 0px, hsla(220, 25%, 10%, 0.8) 0px 8px 16px -5px',
}

/**
 * Component overrides adapted from MUI's Dashboard template
 * (`shared-theme/customizations/*` and `dashboard/theme/customizations/dataGrid`,
 * v9.4.0). The template's look lives here far more than in its dashboard
 * screens, which is why this file exists and the screens were not copied.
 *
 * Four of the template's overrides are deliberately **not** adopted, each
 * because it would cost something this app has already paid for:
 *
 * - **`MuiAlert`** paints every alert amber regardless of severity. This app
 *   routes info/warning/error through `Alert` severity (web/README.md's
 *   feedback conventions — the replica banner, failed downloads, unreachable
 *   integrations), so a single tone would erase the distinction the surface
 *   exists to make.
 * - **`MuiLink`** recolours links to `text.primary` and replaces the underline
 *   with a rule that *disappears* on hover. In a wiki, links inside prose are
 *   the main affordance, and colour-plus-nothing is the shape of a WCAG 1.4.1
 *   failure (`link-in-text-block`, enforced in the browser a11y tier).
 * - **`MuiIconButton`** gives every icon button a border and a filled box. That
 *   suits the template, whose only icon buttons sit in the header; here it
 *   would also box every tree chevron and every remove-row button. The app bar
 *   cluster gets that treatment locally instead (AppShell.tsx).
 * - **`MuiCheckbox`**, **`MuiSelect`** (a second bordered box around an already
 *   bordered input), **`MuiFormLabel`**, **`MuiLinearProgress`**, `MuiTab*`,
 *   `MuiStep*` and `MuiPagination*` are dropped as unused or redundant here.
 *
 * Where an adopted override is changed rather than copied, the reason is in a
 * comment beside it. The commonest reason is that the template writes styles a
 * label-less input can afford and this app's forms cannot.
 */
export const componentCustomizations: Components<Theme> = {
  MuiButtonBase: {
    defaultProps: {
      disableTouchRipple: true,
      disableRipple: true,
    },
    styleOverrides: {
      root: ({ theme }) => ({
        boxSizing: 'border-box',
        transition: 'all 100ms ease-in',
        '&:focus-visible': {
          outline: `3px solid ${alpha(theme.palette.primary.main, 0.5)}`,
          outlineOffset: '2px',
        },
      }),
    },
  },

  MuiButton: {
    defaultProps: {
      disableElevation: true,
    },
    styleOverrides: {
      root: ({ theme }) => ({
        boxShadow: 'none',
        borderRadius: theme.shape.borderRadius,
        textTransform: 'none',
        variants: [
          // `minHeight`, where the template fixes `height`: a label that wraps
          // in a narrow dialog would otherwise spill out of the button.
          { props: { size: 'small' }, style: { minHeight: '2.25rem', padding: '8px 12px' } },
          { props: { size: 'medium' }, style: { minHeight: '2.5rem' } },
          {
            // The template's signature CTA is neutral, not brand-coloured. Its
            // fill is a gradient; this is the flat midpoint of that gradient,
            // because a background *image* is unreadable to axe's contrast
            // check and would drop every primary button out of automated 1.4.3
            // coverage — the same trap as MUI's dark elevation overlay
            // (docs/ACCESSIBILITY.md).
            props: { color: 'primary', variant: 'contained' },
            style: {
              color: '#ffffff',
              backgroundColor: gray[800],
              border: `1px solid ${gray[700]}`,
              '&:hover': { backgroundColor: gray[700] },
              '&:active': { backgroundColor: gray[900] },
              ...theme.applyStyles('dark', {
                color: '#000000',
                backgroundColor: gray[50],
                border: `1px solid ${gray[200]}`,
                '&:hover': { backgroundColor: gray[200] },
                '&:active': { backgroundColor: gray[300] },
              }),
            },
          },
          {
            // Scoped to `primary` where the template applies it to every
            // outlined button: an outlined `color="error"` action must keep
            // reading as destructive rather than turning neutral.
            props: { color: 'primary', variant: 'outlined' },
            style: {
              color: theme.palette.text.primary,
              borderColor: gray[200],
              backgroundColor: alpha(gray[50], 0.3),
              '&:hover': { backgroundColor: gray[100], borderColor: gray[300] },
              '&:active': { backgroundColor: gray[200] },
              ...theme.applyStyles('dark', {
                backgroundColor: gray[800],
                borderColor: gray[700],
                '&:hover': { backgroundColor: gray[900], borderColor: gray[600] },
                '&:active': { backgroundColor: gray[900] },
              }),
            },
          },
          {
            props: { color: 'primary', variant: 'text' },
            style: {
              color: gray[600],
              '&:hover': { backgroundColor: gray[100] },
              '&:active': { backgroundColor: gray[200] },
              ...theme.applyStyles('dark', {
                color: gray[50],
                '&:hover': { backgroundColor: gray[700] },
                '&:active': { backgroundColor: alpha(gray[700], 0.7) },
              }),
            },
          },
        ],
      }),
    },
  },

  MuiToggleButton: {
    styleOverrides: {
      // Only the shouting is removed. The template's toggle buttons are
      // 12px/16px pills built for a settings row; these are editor-toolbar
      // controls, where that padding would double the toolbar's height.
      root: { textTransform: 'none' },
    },
  },

  MuiInputBase: {
    styleOverrides: {
      input: {
        // The template dims the placeholder to 0.7. Full opacity on the same
        // grey clears 4.5:1 instead of landing near 3:1, and looks the same
        // next to the field's own text.
        '&::placeholder': { opacity: 1, color: gray[500] },
      },
    },
  },

  MuiOutlinedInput: {
    styleOverrides: {
      root: ({ theme }) => ({
        // The template deletes the notched outline and draws its own border on
        // the root, which only works for fields whose label sits above them.
        // Nearly every field here is a `TextField label="…"`, whose floating
        // label needs the notch to cut through the border. So: the template's
        // recessed fill, soft border colour and focus glow, on the outline MUI
        // already draws.
        backgroundColor: theme.palette.background.default,
        transition: 'border-color 120ms ease-in',
        [`& .${outlinedInputClasses.notchedOutline}`]: {
          borderColor: theme.palette.divider,
        },
        [`&:hover .${outlinedInputClasses.notchedOutline}`]: {
          borderColor: gray[400],
        },
        [`&.${outlinedInputClasses.focused}`]: {
          outline: `3px solid ${alpha(theme.palette.primary.main, 0.5)}`,
          [`& .${outlinedInputClasses.notchedOutline}`]: { borderWidth: 1 },
        },
        [`&.${outlinedInputClasses.error}.${outlinedInputClasses.focused}`]: {
          outline: `3px solid ${alpha(theme.palette.error.main, 0.5)}`,
        },
        ...theme.applyStyles('dark', {
          [`&:hover .${outlinedInputClasses.notchedOutline}`]: { borderColor: gray[500] },
        }),
      }),
    },
  },

  MuiInputAdornment: {
    styleOverrides: {
      root: ({ theme }) => ({
        color: gray[500],
        ...theme.applyStyles('dark', { color: gray[400] }),
      }),
    },
  },

  MuiPopover: {
    styleOverrides: {
      // Menus are Popovers wearing a second class, so this styles both the
      // account/overflow menus and the notification popover from one rule.
      paper: ({ theme }) => ({
        marginTop: 4,
        borderRadius: theme.shape.borderRadius,
        border: `1px solid ${theme.palette.divider}`,
        boxShadow: softShadow.light,
        ...theme.applyStyles('dark', { boxShadow: softShadow.dark }),
      }),
    },
  },

  MuiMenuItem: {
    styleOverrides: {
      root: ({ theme }) => ({
        borderRadius: theme.shape.borderRadius,
        padding: '6px 8px',
        // The template's list rows get their icon/label spacing from a gap on
        // the row rather than from a wide `ListItemIcon`; menu items need the
        // same gap once that width is gone.
        gap: theme.spacing(1),
      }),
    },
  },

  MuiDrawer: {
    styleOverrides: {
      // The template's navigation rail sits one shade off the content region.
      // In dark mode `background.paper` already is that shade, so only light
      // mode needs saying.
      paper: ({ theme }) => theme.applyStyles('light', { backgroundColor: gray[50] }),
    },
  },

  MuiList: {
    styleOverrides: {
      root: {
        // As a variant rather than a flat rule, so `<List disablePadding>`
        // still means what it says — theme overrides are applied after a
        // component's own styles and would otherwise win over it.
        variants: [{ props: { disablePadding: false }, style: { padding: 8 } }],
      },
    },
  },

  MuiListItemButton: {
    styleOverrides: {
      root: ({ theme }) => ({
        borderRadius: theme.shape.borderRadius,
        gap: theme.spacing(1),
        padding: '2px 8px',
        // The template's rows carry a 1rem icon in `text.secondary`. Only the
        // icon: it applies the same de-emphasis to the label, and this app's
        // ListItemButtons are search hits and page lists as well as navigation,
        // where the label is the content and must not be dimmed.
        [`& .${svgIconClasses.root}`]: {
          fontSize: '1rem',
          color: theme.palette.text.secondary,
        },
        '&.Mui-selected': {
          [`& .${svgIconClasses.root}`]: { color: theme.palette.text.primary },
          // A weight change as well as a fill: the template's selected state is
          // carried by background alone, and "which page am I on" is exactly
          // the kind of persistent information WCAG 1.4.1 wants a second cue
          // for.
          [`& .${listItemTextClasses.primary}`]: { fontWeight: 600 },
        },
      }),
    },
  },

  MuiListItemText: {
    styleOverrides: {
      primary: ({ theme }) => ({
        fontSize: theme.typography.body2.fontSize,
        fontWeight: 500,
        lineHeight: theme.typography.body2.lineHeight,
      }),
      secondary: ({ theme }) => ({
        fontSize: theme.typography.caption.fontSize,
        lineHeight: theme.typography.caption.lineHeight,
      }),
    },
  },

  MuiListItemIcon: {
    styleOverrides: {
      // Zero, as the template has it: the icon sits against the label and the
      // gap comes from the row (`MuiListItemButton`, `MuiMenuItem` above),
      // which is what makes its rows read as tight pills rather than as a
      // fixed icon gutter.
      root: { minWidth: 0 },
    },
  },

  MuiListSubheader: {
    styleOverrides: {
      root: ({ theme }) => ({
        backgroundColor: 'transparent',
        padding: '4px 8px',
        fontSize: theme.typography.caption.fontSize,
        fontWeight: 500,
        lineHeight: theme.typography.caption.lineHeight,
      }),
    },
  },

  MuiChip: {
    defaultProps: { size: 'small' },
    styleOverrides: {
      root: ({ theme }) => ({
        borderRadius: '999px',
        [`& .${chipClasses.label}`]: { fontWeight: 600 },
        variants: [
          {
            // The tinted fills are scoped to `filled` where the template
            // applies them to every chip. The audit log picks filled-vs-outlined
            // to say how loud an outcome is (DENIED filled, SUCCESS outlined),
            // and one treatment for both would flatten that back out.
            props: { variant: 'filled', color: 'default' },
            style: {
              border: '1px solid',
              borderColor: gray[200],
              backgroundColor: gray[100],
              [`& .${chipClasses.label}`]: { color: gray[500] },
              [`& .${chipClasses.icon}`]: { color: gray[500] },
              ...theme.applyStyles('dark', {
                borderColor: gray[700],
                backgroundColor: gray[800],
                [`& .${chipClasses.label}`]: { color: gray[300] },
                [`& .${chipClasses.icon}`]: { color: gray[300] },
              }),
            },
          },
          {
            props: { variant: 'filled', color: 'success' },
            style: {
              border: '1px solid',
              borderColor: green[200],
              backgroundColor: green[50],
              [`& .${chipClasses.label}`]: { color: green[500] },
              [`& .${chipClasses.icon}`]: { color: green[500] },
              ...theme.applyStyles('dark', {
                borderColor: green[800],
                backgroundColor: green[900],
                [`& .${chipClasses.label}`]: { color: green[300] },
                [`& .${chipClasses.icon}`]: { color: green[300] },
              }),
            },
          },
          {
            props: { variant: 'filled', color: 'error' },
            style: {
              border: '1px solid',
              borderColor: red[100],
              backgroundColor: red[50],
              [`& .${chipClasses.label}`]: { color: red[500] },
              [`& .${chipClasses.icon}`]: { color: red[500] },
              ...theme.applyStyles('dark', {
                borderColor: red[800],
                backgroundColor: red[900],
                [`& .${chipClasses.label}`]: { color: red[200] },
                [`& .${chipClasses.icon}`]: { color: red[300] },
              }),
            },
          },
          {
            props: { size: 'small' },
            style: {
              // 24, not the template's 20: a chip rendered as an Autocomplete
              // tag carries a delete affordance, and WCAG 2.2's 2.5.8 floor for
              // a target is 24x24 (the rule builder's group and attribute
              // pickers are full of them).
              maxHeight: 24,
              [`& .${chipClasses.label}`]: { fontSize: theme.typography.caption.fontSize },
              [`& .${svgIconClasses.root}`]: { fontSize: theme.typography.caption.fontSize },
            },
          },
          {
            props: { size: 'medium' },
            style: {
              [`& .${chipClasses.label}`]: { fontSize: theme.typography.caption.fontSize },
            },
          },
        ],
      }),
    },
  },

  MuiDialog: {
    styleOverrides: {
      root: ({ theme }) => ({
        '& .MuiDialog-paper': {
          borderRadius: '10px',
          border: '1px solid',
          borderColor: theme.palette.divider,
        },
      }),
    },
  },

  MuiAppBar: {
    defaultProps: {
      elevation: 0,
    },
  },

  MuiPaper: {
    styleOverrides: {
      root: {
        // Flat surfaces in dark mode: MUI's dark elevation overlay is a
        // background-image gradient, which (a) doesn't fit the app's flat look
        // (AppBar/buttons already disable elevation) and (b) makes text on any
        // elevated Paper unverifiable by contrast tooling — axe abstains on
        // gradient backgrounds, so the app bar and drawer would silently drop
        // out of the automated 1.4.3 checks (docs/ACCESSIBILITY.md). The
        // template's own surfaces customization does not reintroduce it; its
        // menu paper spells `backgroundImage: 'none'` out for the same reason.
        backgroundImage: 'none',
      },
    },
  },

  MuiDataGrid: {
    styleOverrides: {
      // Class names as strings rather than `gridClasses` from
      // `@mui/x-data-grid`: the theme is in the entry bundle and the grid is
      // deliberately not (router.tsx lazy-loads the routes that use it), so
      // importing its runtime here would undo that split.
      root: ({ theme }) => ({
        overflow: 'clip',
        borderColor: theme.palette.divider,
        backgroundColor: theme.palette.background.default,
        '& .MuiDataGrid-columnHeader': { backgroundColor: theme.palette.background.paper },
        '& .MuiDataGrid-footerContainer': { backgroundColor: theme.palette.background.paper },
      }),
      cell: ({ theme }) => ({ borderTopColor: theme.palette.divider }),
      row: ({ theme }) => ({
        '&:last-of-type': { borderBottom: `1px solid ${theme.palette.divider}` },
        '&:hover': { backgroundColor: theme.palette.action.hover },
        '&.Mui-selected': {
          background: theme.palette.action.selected,
          '&:hover': { backgroundColor: theme.palette.action.hover },
        },
      }),
      menu: ({ theme }) => ({
        borderRadius: theme.shape.borderRadius,
        backgroundImage: 'none',
        '& .MuiPaper-root': { border: `1px solid ${theme.palette.divider}` },
        '& .MuiMenuItem-root': { margin: '0 4px' },
      }),
      filterForm: ({ theme }) => ({ gap: theme.spacing(1), alignItems: 'flex-end' }),
      columnsManagementHeader: ({ theme }) => ({
        paddingRight: theme.spacing(3),
        paddingLeft: theme.spacing(3),
      }),
      columnHeaderTitleContainer: { flexGrow: 1, justifyContent: 'space-between' },
    },
  },
}
