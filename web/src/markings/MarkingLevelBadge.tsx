import { Box } from '@mui/material'
import { visuallyHidden } from '@mui/utils'
import type { ClassificationLevel } from '../graphql/generated/graphql'
import { markingTone } from './markingTone'

export interface MarkingLevelBadgeProps {
  /** Styling only (markingTone) — nothing readable is derived from it. */
  level: ClassificationLevel
  /**
   * `PageMarkingView.levelName` — the server's display spelling for this
   * level (`OFFICIAL-SENSITIVE`, `TOP SECRET`), from the same
   * `ProtectiveMarking.LevelName` every other consumer reads.
   *
   * Deliberately NOT `label`: `label` is the whole marking, and the two are
   * not interchangeable. See the component doc.
   */
  levelName: string
}

/**
 * The compact classification badge for surfaces that LIST pages — search
 * results and the space browser's page tree — so a reader scanning a list
 * sees how sensitive each hit is without opening it (design.md §21).
 *
 * **This shows a level, not a marking, and the distinction is load-bearing.**
 * A list row has no space for a caveat, so a page marked
 * `SECRET [GB EYES ONLY]` badges here as `SECRET` — informational, and short
 * of the truth by exactly the caveat. That is tolerable only because the
 * badge is not the control (the caveat is still enforced server-side on every
 * read, §21.2) and because the page's own banners render the whole `label`.
 * It is why this component takes `levelName` and has no way to accept
 * `label`: a caller who wants to present "this page's marking" must use
 * MarkingBanner instead, and the prop types make the wrong choice hard to
 * reach by accident.
 *
 * The spelling itself comes from the server (`PageMarkingView.levelName`,
 * §21.1). Deriving `OFFICIAL-SENSITIVE` from the `OFFICIAL_SENSITIVE` wire
 * name in TypeScript would be a second implementation of the display form,
 * which is precisely the drift §21.1's "one method each, in
 * `ProtectiveMarking`" exists to prevent.
 *
 * Text, not colour (WCAG 1.4.1): the tone from markingTone.ts makes a higher
 * classification louder beside the word, and carries nothing the word does
 * not already say.
 */
export function MarkingLevelBadge({ level, levelName }: MarkingLevelBadgeProps) {
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
      {levelName}
    </Box>
  )
}
