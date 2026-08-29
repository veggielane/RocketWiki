import { Box, Typography } from '@mui/material'
import { visuallyHidden } from '@mui/utils'
import type { ClassificationLevel } from '../graphql/generated/graphql'
import { markingTone } from './markingTone'

export interface ClassificationBannerProps {
  /**
   * The server-built display string, rendered verbatim — `PageMarkingView.label`
   * or an `AggregateMarkingLabel.label` (§21.13). design.md §21.4/§21.12 put the
   * one formatter server-side precisely so the SPA, an MCP client and an audit
   * reviewer read identical text, so this component never sees prefix, level and
   * caveat separately and could not compose them if it wanted to. The ICDS
   * component takes the country prefix as a separate prop; ours arrives already
   * folded into the label, which is the stronger guarantee.
   */
  label: string
  /** Styling only (markingTone) — the meaning is all in `label`. */
  level: ClassificationLevel
  /**
   * ICDS's `inline` prop. False (the default) fixes the banner to the bottom of
   * the viewport, which is the pattern's whole point: the classification of what
   * you are looking at stays on screen while you scroll. True renders it in the
   * flow, for a marking that describes a *part* of the page — a search result
   * set, a page-list widget — rather than the page itself.
   */
  inline?: boolean
  /**
   * What this marking is about, for screen readers. Not decoration: "this page"
   * and "this answer" are different claims, and a reader who cannot see where
   * the banner sits has nothing else to tell them apart.
   */
  scopeLabel: string
}

/** Height reserved so fixed-position banners never sit on top of page content. */
export const CLASSIFICATION_BANNER_HEIGHT = 32

/**
 * A protective marking (design.md §21), following the Intelligence Community
 * Design System's classification banner:
 * https://design.sis.gov.uk/components/utility/classification-banner/
 *
 * ICDS specifies a SINGLE banner fixed to the bottom of the viewport, which does
 * not scroll with the page, announced to screen readers as a landmark region
 * with a hidden label. That replaces this app's earlier top-and-bottom pair.
 * The pair existed for a real reason — someone printing or screenshotting a long
 * page has to meet the marking without knowing to scroll — so that reason is kept
 * rather than dropped: the print-only banner below renders the same label at the
 * top of the printed document, and the fixed banner falls back into the flow when
 * printing so it lands at the end. On screen you get ICDS's behaviour; on paper
 * you still get the marking top and bottom.
 *
 * Colour is an accent, never a signal (WCAG 1.4.1): the label is always written
 * out, and markingTone only makes a higher classification louder beside it. The
 * tones here are this project's own measured contrast pairs rather than ICDS's
 * palette — see markingTone.ts for the measured ratios, which the CI a11y tier
 * checks on real painted pixels.
 */
export function ClassificationBanner({ label, level, inline = false, scopeLabel }: ClassificationBannerProps) {
  return (
    <>
      <Box
        // A landmark only when fixed. ICDS announces the banner as a region, and
        // that works because there is exactly one — two landmarks sharing a name
        // is an axe `landmark-unique` failure, which is what an inline marking
        // beside a fixed one would create.
        {...(inline ? {} : { component: 'section' as const, 'aria-label': scopeLabel })}
        data-classification-banner={inline ? 'inline' : 'fixed'}
        sx={(theme) => {
          const tone = markingTone(level, theme.palette.mode === 'dark' ? 'dark' : 'light')
          return {
            backgroundColor: tone.bg,
            color: tone.fg,
            borderTop: inline ? 'none' : `${tone.borderWidth}px solid ${tone.border}`,
            border: inline ? `${tone.borderWidth}px solid ${tone.border}` : undefined,
            borderRadius: inline ? 1 : 0,
            px: 2,
            py: inline ? 1 : 0.5,
            textAlign: 'center',
            ...(inline
              ? {}
              : {
                  position: 'fixed',
                  left: 0,
                  right: 0,
                  bottom: 0,
                  // Above the content, below MUI's modals so a dialog is never
                  // obscured by it.
                  zIndex: theme.zIndex.drawer + 1,
                  minHeight: CLASSIFICATION_BANNER_HEIGHT,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }),
            // Printing: let the fixed banner fall back into the flow so it ends
            // up at the foot of the document instead of vanishing.
            '@media print': { position: 'static' },
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
            fontSize: inline ? undefined : '0.8125rem',
            // The server's label is already the marking's written form (§21.4)
            // — no text-transform, so what is read is byte-for-byte what was
            // stored.
            overflowWrap: 'anywhere',
          }}
        >
          <Box component="span" sx={visuallyHidden}>
            {scopeLabel}:{' '}
          </Box>
          {label}
        </Typography>
      </Box>

      {/* Print only, and only for the fixed variant: the marking at the TOP of
          the printed document, which the earlier top-and-bottom pair guaranteed
          and a viewport-fixed banner cannot. aria-hidden because on screen it is
          not rendered at all and the fixed banner above already announces it. */}
      {!inline && (
        <Box
          aria-hidden
          data-classification-banner="print-head"
          sx={(theme) => {
            const tone = markingTone(level, theme.palette.mode === 'dark' ? 'dark' : 'light')
            return {
              display: 'none',
              '@media print': {
                display: 'block',
                backgroundColor: tone.bg,
                color: tone.fg,
                textAlign: 'center',
                fontWeight: 700,
                letterSpacing: '0.08em',
                px: 2,
                py: 0.5,
              },
            }
          }}
        >
          {label}
        </Box>
      )}
    </>
  )
}
