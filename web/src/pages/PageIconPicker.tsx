import { Box, MenuItem, TextField } from '@mui/material'
import ArticleOutlinedIcon from '@mui/icons-material/ArticleOutlined'
import type { PageIcon } from '../graphql/generated/graphql'
import { lookupPageIcon, PAGE_ICON_OPTIONS, type PageIconEntry } from './pageIcons'

/**
 * The sentinel for "no icon", because `''` is not usable here: MUI treats an
 * empty Select value as unfilled, which leaves the floating label sitting on
 * top of the option text rather than shrinking above it. Lowercase, so it can
 * never collide with a `PageIcon` member — the schema's are SCREAMING_CASE.
 */
const NO_ICON = '__none__'

export interface PageIconPickerProps {
  value: PageIcon | null
  onChange: (icon: PageIcon | null) => void
  disabled?: boolean
  size?: 'small' | 'medium'
}

/**
 * The page-icon control, shared by create and edit so the two cannot offer
 * different sets or spell the same icon differently.
 *
 * "No icon" is an option in the list, not the absence of one: an icon is
 * nullable everywhere on the wire, so unsetting has to be something a user can
 * *do*, and a picker you can only ever add to is a one-way door.
 *
 * A Select rather than a grid of toggle buttons: it is the same control the
 * parent-page picker beside it already uses, it names every option in text
 * (type-ahead finds "rocket" without hunting for the glyph), and it needs no
 * hand-built roving focus to be keyboard-operable.
 */
export function PageIconPicker({ value, onChange, disabled = false, size = 'medium' }: PageIconPickerProps) {
  // A name this build has no glyph for still has to stay selected. The value
  // is typed as a `PageIcon`, but the wire is the authority (see
  // pageIcons.tsx's lookup) — dropping the option would leave the Select
  // showing "No icon", and saving would then silently clear an icon the user
  // never touched.
  const unrecognised = value !== null && lookupPageIcon(value) === undefined ? value : null

  return (
    <TextField
      select
      label="Icon"
      size={size}
      disabled={disabled}
      value={value ?? NO_ICON}
      onChange={(e) => onChange(e.target.value === NO_ICON ? null : (e.target.value as PageIcon))}
      sx={{ minWidth: 200 }}
    >
      <MenuItem value={NO_ICON}>No icon</MenuItem>
      {unrecognised !== null && (
        <MenuItem value={unrecognised}>
          <IconOption label={unrecognised} Icon={ArticleOutlinedIcon} />
        </MenuItem>
      )}
      {PAGE_ICON_OPTIONS.map(({ value: option, label, Icon }) => (
        <MenuItem key={option} value={option}>
          <IconOption label={label} Icon={Icon} />
        </MenuItem>
      ))}
    </TextField>
  )
}

/**
 * One row of the list — and, since MUI renders the selected option's children
 * into the closed field too, also what the field shows once chosen.
 *
 * A span rather than ListItemIcon/ListItemText: the closed field is not a
 * list, and the list semantics would come out as a stray listitem there. The
 * glyph stays decorative (MUI marks its svg aria-hidden), so the row's
 * accessible name is the label alone.
 */
function IconOption({ label, Icon }: { label: string; Icon: PageIconEntry['Icon'] }) {
  return (
    <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 1 }}>
      <Icon fontSize="small" />
      {label}
    </Box>
  )
}
