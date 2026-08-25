import { Box, Typography } from '@mui/material'
import { visuallyHidden } from '@mui/utils'
import type { ClassificationLevel } from '../graphql/generated/graphql'
import { markingTone } from './markingTone'

export interface MarkingBannerProps {
  /**
   * `PageMarkingView.label` — the server-built display string, rendered
   * verbatim. design.md §21.4/§21.12 put the one formatter
   * (`ProtectiveMarking.Format`) server-side precisely so the SPA, an MCP
   * client and an audit reviewer read identical text, so this component
   * never sees prefix/level/caveat separately and could not compose them if
   * it wanted to.
   */
  label: string
  /** Styling only (markingTone) — the meaning is all in `label`. */
  level: ClassificationLevel
  /**
   * Which of the pair this is. Both render the same string; the placement
   * only changes the visually-hidden lead-in, so a screen-reader user meeting
   * the marking a second time is told it is the same one rather than being
   * left to wonder whether the page changed classification part-way down.
   *
   * `section` is the marking control's own read-out, where the surrounding
   * heading already says "Protective marking" — a lead-in there would make a
   * screen reader say it twice in a row.
   */
  placement: 'head' | 'foot' | 'section'
}

const SCREEN_READER_LEAD_IN: Record<MarkingBannerProps['placement'], string | null> = {
  head: 'Protective marking: ',
  foot: 'Protective marking, repeated at the foot of the page: ',
  section: null,
}

/**
 * A page's protective marking (design.md §21), rendered at the top AND the
 * bottom of the page view. That pairing is the convention and it is
 * deliberate: someone printing or screenshotting a long page has to see the
 * marking without knowing to scroll to one particular spot.
 *
 * Deliberately NOT `role="alert"`, and not a landmark. A marking is not an
 * event — it is persistent context that belongs to the page the way a title
 * does, so it is announced when a reader reaches it in document order and
 * never interrupts. (Two landmarks carrying the same name would also be an
 * axe `landmark-unique` failure, and giving each a different name would say
 * the two banners were different things when they are the same one twice.)
 */
export function MarkingBanner({ label, level, placement }: MarkingBannerProps) {
  return (
    <Box
      // Not styling and not semantics — the one hook by which a test can
      // assert BOTH banners exist and carry the same string, which is the
      // convention §21 is actually asking for.
      data-marking-placement={placement}
      sx={(theme) => {
        const tone = markingTone(level, theme.palette.mode === 'dark' ? 'dark' : 'light')
        return {
          backgroundColor: tone.bg,
          color: tone.fg,
          border: `${tone.borderWidth}px solid ${tone.border}`,
          borderRadius: 1,
          px: 2,
          py: 1,
          textAlign: 'center',
        }
      }}
    >
      <Typography
        component="p"
        sx={{
          // Inherit the tone's foreground rather than a palette token: the
          // contrast pair in markingTone.ts is measured as a pair, and a
          // typography colour would break it apart.
          color: 'inherit',
          fontWeight: 700,
          letterSpacing: '0.08em',
          // The server's label is already the marking's written form (§21.4)
          // — no text-transform, so what is read is byte-for-byte what was
          // stored.
          overflowWrap: 'anywhere',
        }}
      >
        {SCREEN_READER_LEAD_IN[placement] !== null && (
          <Box component="span" sx={visuallyHidden}>
            {SCREEN_READER_LEAD_IN[placement]}
          </Box>
        )}
        {label}
      </Typography>
    </Box>
  )
}
