import { Box } from '@mui/material'
import { visuallyHidden } from '@mui/utils'
import type { ClassificationLevel } from '../graphql/generated/graphql'
import { markingTone } from './markingTone'

export interface MarkingLabelChipProps {
  /**
   * `PageMarkingView.label` — the server's whole marking, rendered verbatim.
   * The one formatter is server-side (design.md §21.4/§21.12), so this
   * component never sees prefix, level, selectors and caveat separately and
   * could not compose them if it wanted to.
   */
  label: string
  /** Styling only (markingTone) — the meaning is all in `label`. */
  level: ClassificationLevel
}

/**
 * The compact WHOLE-marking chip for a placeholder row: a protected tree
 * leaf (design.md §21.8), where the label is the one fact about the page the
 * caller is allowed — and needs — to see.
 *
 * Deliberately a sibling of `MarkingLevelBadge` rather than a mode of it.
 * That badge shows the LEVEL of a page the caller can open, where the whole
 * label waits on the page's own banners; here there is no page to open, so
 * the chip has to carry the label itself, and the two components' prop types
 * keep "level where a marking is claimed" from happening by accident.
 *
 * Text, not colour (WCAG 1.4.1): the tone only makes a higher classification
 * louder beside the words. The lead-in is for a screen reader meeting a
 * marking mid-list, so it is announced as one rather than as stray capitals.
 */
export function MarkingLabelChip({ label, level }: MarkingLabelChipProps) {
  return (
    <Box
      component="span"
      sx={(theme) => {
        const tone = markingTone(level, theme.palette.mode === 'dark' ? 'dark' : 'light')
        return {
          display: 'inline-block',
          flexShrink: 0,
          backgroundColor: tone.bg,
          color: tone.fg,
          border: `${tone.borderWidth}px solid ${tone.border}`,
          borderRadius: 1,
          px: 0.75,
          py: 0.125,
          fontSize: '0.6875rem',
          fontWeight: 700,
          letterSpacing: '0.04em',
          lineHeight: 1.6,
          whiteSpace: 'nowrap',
        }
      }}
    >
      <Box component="span" sx={visuallyHidden}>
        Protective marking:{' '}
      </Box>
      {label}
    </Box>
  )
}
