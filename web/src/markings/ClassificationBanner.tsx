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
   * ICDS's `inline` prop. False (the default) is the banner for the screen as a
   * whole: the shell renders it as its bottom row, so it is on screen without
   * scrolling for as long as the screen is, which is the pattern's whole point.
   * A screen does not render this variant itself — it declares its marking
   * with `useClassificationBanner` and the shell places it (see
   * classificationBannerContext.ts). True renders it in the flow, for a marking
   * that describes a *part* of the page — a search result set, a page-list
   * widget — rather than the page itself.
   */
  inline?: boolean
  /**
   * What this marking is about, for screen readers. Not decoration: "this page"
   * and "this answer" are different claims, and a reader who cannot see where
   * the banner sits has nothing else to tell them apart.
   */
  scopeLabel: string
}

/**
 * The strip's minimum height. Its own, and nothing else's: the banner is a row
 * of the shell's layout, so no other surface has to reserve space for it, and
 * nothing outside this file should need the number.
 */
const CLASSIFICATION_BANNER_HEIGHT = 32

/**
 * A protective marking (design.md §21), following the Intelligence Community
 * Design System's classification banner:
 * https://design.sis.gov.uk/components/utility/classification-banner/
 *
 * ICDS specifies a SINGLE banner at the bottom of the viewport, which does not
 * scroll with the page, announced to screen readers as a landmark region with
 * a hidden label. That replaces this app's earlier top-and-bottom pair.
 *
 * ICDS gets "does not scroll" with `position: fixed`. This one gets it by being
 * the last row of the shell's full-height column (AppShell.tsx), with the
 * scrolling content region in the row above it. Same result on screen — full
 * width, flush to the bottom, unmoved by scrolling — with one difference that
 * is the reason for it: a fixed strip floats OVER the bottom of the content
 * region, so every control that could end up there (the editor's sticky save
 * bar, the rail's account block, a focused element scrolled into view) had to
 * be lifted or padded clear of it by hand, and the one that was not — a task
 * checkbox under a save bar that had been lifted for the banner — was a WCAG
 * 2.5.8 failure. A layout row has nothing underneath it, so there is nothing
 * to clear.
 *
 * The pair existed for a real reason — someone printing or screenshotting a
 * long page has to meet the marking without knowing to scroll — so that reason
 * is kept rather than dropped: the print-only banner below renders the same
 * label at the top of the printed document, and this one is already in the
 * flow so it lands at the end. On screen you get ICDS's behaviour; on paper you
 * still get the marking top and bottom.
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
        // A landmark only for the screen's banner. ICDS announces the banner as
        // a region, and that works because there is exactly one — two landmarks
        // sharing a name is an axe `landmark-unique` failure, which is what an
        // inline marking beside the screen's would create.
        {...(inline ? {} : { component: 'section' as const, 'aria-label': scopeLabel })}
        data-classification-banner={inline ? 'inline' : 'foot'}
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
                  // A row of the shell's column: never squeezed by the content
                  // region above it, however tall that wants to be.
                  flexShrink: 0,
                  minHeight: CLASSIFICATION_BANNER_HEIGHT,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  // Printed, it is the foot of the document: kept whole, and
                  // kept with the content's last lines rather than alone on
                  // a fresh sheet where the engine can manage it.
                  '@media print': { breakInside: 'avoid', breakBefore: 'avoid' },
                }),
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

      {/* Print only, and only for the screen's banner: the marking at the TOP of
          the printed document, which the earlier top-and-bottom pair guaranteed
          and a bottom-of-screen banner cannot. aria-hidden because on screen it
          is not rendered at all and the banner above already announces it. */}
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
                // The shell renders this component as the last child of its
                // flex column, so `order` is what moves this copy to the head
                // of the printed page while the banner proper stays at its
                // foot. Print-only and aria-hidden, so it is not the CSS
                // reordering 2.4.3 warns about: nothing focusable, nothing
                // read, and no screen rendering at all.
                order: -1,
                breakInside: 'avoid',
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
