import { Box } from '@mui/material'
import { visuallyHidden } from '@mui/utils'
import type { ClassificationLevel } from '../graphql/generated/graphql'
import { markingTone } from './markingTone'

export interface MarkingLevelBadgeProps {
  level: ClassificationLevel
}

/**
 * The compact classification badge for surfaces that LIST pages — search
 * results and the space browser's page tree — so a reader scanning a list
 * sees how sensitive each hit is without opening it (design.md §21).
 *
 * The level only, never the full label: a list row has no room for the
 * caveat, and the page's own banners are where the whole marking is read.
 *
 * **The level is rendered as its wire name, verbatim.** §21.1 keeps three
 * spellings of a level deliberately distinct — wire name, display name,
 * reason token — with "one method each, in `ProtectiveMarking`, so they
 * cannot drift". Prettifying `OFFICIAL_SENSITIVE` into `OFFICIAL-SENSITIVE`
 * here would be a second implementation of the display spelling living in
 * TypeScript, which is the drift that section exists to prevent. The wire
 * name is what the enum, the clearance claim, the sync payload and the audit
 * row all say, so it is spelled the same way here and transformed not at all.
 *
 * Text, not colour (WCAG 1.4.1): the tone from markingTone.ts makes a higher
 * classification louder beside the word, and carries nothing the word does
 * not already say.
 */
export function MarkingLevelBadge({ level }: MarkingLevelBadgeProps) {
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
        Classification:{' '}
      </Box>
      {level}
    </Box>
  )
}
