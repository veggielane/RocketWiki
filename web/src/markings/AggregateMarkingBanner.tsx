import { Box, Typography } from '@mui/material'
import type { AggregateMarkingFragment } from '../graphql/generated/graphql'
import { MarkingBanner } from './MarkingBanner'

export type AggregateMarkingPlacement = 'answer-head' | 'answer-foot' | 'results' | 'page-list'

export interface AggregateMarkingBannerProps {
  /**
   * The payload's `aggregateMarking` exactly as it arrived — **including
   * when it is null**, which is why this prop is not narrowed at the call
   * site. See the component doc for what null means.
   */
  marking: AggregateMarkingFragment | null | undefined
  placement: AggregateMarkingPlacement
}

/**
 * What the label covers — a scope statement, not guidance. Both surfaces have
 * the same honesty problem: the aggregate legitimately out-ranks everything
 * on screen, and without this note a reader would read the banner as a claim
 * about the rows or citations they can see and conclude it was simply wrong.
 *
 * - An Ask answer's aggregate spans everything that entered the model
 *   context, not merely what got cited (§21.13) — retrieved content that
 *   shaped the answer without earning a citation shaped it anyway.
 * - A search aggregate spans the whole permission-filtered hit set, the same
 *   set `totalCount` counts, so a SECRET hit at position 95 correctly labels
 *   a screen showing only OFFICIAL rows.
 * - A page-list widget's aggregate spans the whole permission-filtered result
 *   set of its RQL query (§22.6), which the fence's own `limit` routinely cuts
 *   short — so the label can out-rank every badge the widget drew.
 *
 * Deliberately says only what the label covers. Nothing here explains what a
 * classification permits, or whether anything may be shared — that meaning is
 * the reader's to know, and a wiki is not the place it gets taught.
 *
 * The notes speak about the QUERY's reach, never about the reader's: "not
 * shown here" is about a limit and a page size, and must not drift into
 * anything that would imply results were withheld (§6.7 — a page the reader
 * cannot see is absent, and its absence must stay unremarkable).
 *
 * The foot repeat carries no note: it repeats the marking, and repeating the
 * explanation with it would read as a second, different statement.
 */
const SCOPE_NOTE: Record<AggregateMarkingPlacement, string | null> = {
  'answer-head': 'Covers every page this answer drew on, including any not cited.',
  'answer-foot': null,
  results: 'Covers every result for this search, including any not shown here.',
  'page-list': 'Covers every page matching this query, including any beyond the number listed.',
}

/**
 * What a compilation of marked content is marked (design.md §21.13): the
 * highest classification among everything that fed it. An Ask answer and a
 * search result list are compilations, and before this an answer synthesized
 * from a `UK SECRET` page arrived with no marking at all — the model
 * launders the marking off the content, and this puts it back on.
 *
 * **A display label, not enforcement.** Every contributing page individually
 * passed `canView` and the clearance gate before it reached retrieval, so
 * nothing here gates anything; the label exists to tell a human what the text
 * in front of them *is*.
 *
 * **Null renders nothing, and that is the designed behaviour.** No sources
 * means no label — not OFFICIAL, which would assert a reviewed judgement
 * about content that does not exist; not TOP SECRET, which would invent a
 * fact; and not a placeholder or an "UNMARKED" word, which would be a marking
 * the scheme does not contain. Fail-closed does not apply because nothing is
 * being closed. The absence is the honest output, so this component returns
 * `null` and the surface simply has no banner.
 *
 * The label is rendered verbatim by MarkingBanner and composed nowhere: an
 * aggregate's caveat is a *conjunction* of the distinct source sets
 * (`UK SECRET NZ EYES ONLY, US EYES ONLY` — a reader needs both), which
 * the per-page marking shape cannot express and which client-side assembly
 * would flatten into either a widening union or an empty intersection that
 * reads as no caveat at all.
 */
export function AggregateMarkingBanner({ marking, placement }: AggregateMarkingBannerProps) {
  if (marking == null) return null
  const scope = SCOPE_NOTE[placement]
  return (
    <Box data-aggregate-marking={placement}>
      <MarkingBanner label={marking.label} level={marking.level} placement={placement} />
      {scope !== null && (
        <Typography variant="caption" component="p" color="text.secondary" sx={{ mt: 0.5 }}>
          {scope}
        </Typography>
      )}
    </Box>
  )
}
