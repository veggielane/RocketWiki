import { Box } from '@mui/material'
import { visuallyHidden } from '@mui/utils'
import type { ClassificationLevel } from '../graphql/generated/graphql'
import { markingTone } from './markingTone'

export interface ClearanceBadgeProps {
  /** Styling only (markingTone) — nothing readable is derived from it. */
  level: ClassificationLevel
  /**
   * `UserProfile.clearanceName` — the server's display spelling for the level
   * (`OFFICIAL-SENSITIVE`, `TOP SECRET`), the same `ProtectiveMarking.LevelName`
   * every page marking reads. Never derived here from the wire name.
   */
  levelName: string
}

/**
 * A PERSON's clearance, on their profile. A sibling of `MarkingLevelBadge`
 * rather than a mode of it, for the same reason `MarkingLabelChip` is: that
 * badge announces itself as a page's classification, and a screen reader
 * meeting "Classification: SECRET" beside a person's name would be told the
 * person is marked. The lead-in here says what this is — a clearance — and
 * the prop types keep the two from being swapped by accident.
 *
 * Same tone table and the same treatment as the page badges, so SECRET looks
 * like SECRET wherever it appears. Text, not colour (WCAG 1.4.1): the tone
 * only makes a higher clearance louder beside the word.
 *
 * Only ever rendered for a RECORDED clearance. `clearanceRecorded: false`
 * still carries a level on the wire — the gate's OFFICIAL-SENSITIVE floor —
 * and the profile page shows "Not recorded" in this badge's place rather than
 * badge a default as something the person holds (users/profileCopy.ts).
 */
export function ClearanceBadge({ level, levelName }: ClearanceBadgeProps) {
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
          px: 1,
          py: 0.25,
          fontSize: '0.8125rem',
          fontWeight: 700,
          letterSpacing: '0.04em',
          lineHeight: 1.6,
          whiteSpace: 'nowrap',
        }
      }}
    >
      <Box component="span" sx={visuallyHidden}>
        Clearance:{' '}
      </Box>
      {levelName}
    </Box>
  )
}
