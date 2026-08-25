import { Box, Typography } from '@mui/material'
import { visuallyHidden } from '@mui/utils'
import type { ClassificationLevel } from '../graphql/generated/graphql'
import { markingTone } from './markingTone'

export interface MarkingBannerProps {
  /**
   * `PageMarkingView.label`, or an `AggregateMarkingLabel.label` (§21.13) —
   * either way the server-built display string, rendered verbatim.
   * design.md §21.4/§21.12 put the one formatter
   * (`ProtectiveMarking.FormatLabel`) server-side precisely so the SPA, an
   * MCP client and an audit reviewer read identical text, so this component
   * never sees prefix/level/caveat separately and could not compose them if
   * it wanted to. That matters most for an aggregate, whose caveat is a
   * conjunction (`UK SECRET [GB EYES ONLY] [US EYES ONLY]`) the per-page
   * shape cannot even express.
   */
  label: string
  /** Styling only (markingTone) — the meaning is all in `label`. */
  level: ClassificationLevel
  /**
   * Where on its surface this banner sits. Every placement renders the same
   * string; it only changes the visually-hidden lead-in, so a screen-reader
   * user meeting the marking a second time is told it is the same one rather
   * than being left to wonder whether the content changed classification
   * part-way down — and, for the aggregate placements, is told WHAT is
   * marked, because "this answer" and "this page" are different claims.
   *
   * `section` is the marking control's own read-out, where the surrounding
   * heading already says "Protective marking" — a lead-in there would make a
   * screen reader say it twice in a row.
   */
  placement: 'head' | 'foot' | 'section' | 'answer-head' | 'answer-foot' | 'results'
}

const SCREEN_READER_LEAD_IN: Record<MarkingBannerProps['placement'], string | null> = {
  head: 'Protective marking: ',
  foot: 'Protective marking, repeated at the foot of the page: ',
  section: null,
  // §21.13's compilations. Named for what they mark, not for the page they
  // happen to sit on: an answer synthesized from a UK SECRET page is not
  // "this page's marking", and calling it that would attach the label to the
  // wrong thing for the one reader who cannot see where it sits.
  'answer-head': 'Protective marking for this answer: ',
  'answer-foot': 'Protective marking, repeated at the end of this answer: ',
  results: 'Protective marking for these search results: ',
}

/**
 * A protective marking (design.md §21), rendered at the top AND the bottom
 * of the thing it marks — a page view, or an Ask answer (§21.13). That
 * pairing is the convention and it is deliberate: someone printing,
 * screenshotting or copying a long stretch of marked text has to meet the
 * marking without knowing to scroll to one particular spot.
 *
 * For an aggregate the caller goes through AggregateMarkingBanner, which owns
 * the nullable case and the scope note; this component is the shared
 * rendering underneath both.
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
