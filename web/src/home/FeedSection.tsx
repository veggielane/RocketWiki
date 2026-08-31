import type { ReactNode } from 'react'
import { Alert, Button, List, Paper, Skeleton, Stack, Typography } from '@mui/material'

export interface FeedSectionProps {
  title: string
  /** What the feed is, in one sentence — including anything about it that would otherwise be misread. */
  description: string
  /** How many rows there are in total, where the connection exposes an exact count. */
  totalCount?: number
  /** The noun for the count line: "edit", "page", … */
  countNoun?: string
  shownCount: number
  fetching: boolean
  error: boolean
  /** The shared degradation copy for this feed's own read. */
  failureSummary: string
  /** What to say when the feed is genuinely empty — never a dead end. */
  emptyMessage: ReactNode
  onShowMore?: () => void
  children: ReactNode
}

/**
 * One homepage feed: heading, its own loading, its own error, its own empty.
 *
 * The independence is the point and it is why there are three queries rather
 * than one. A homepage that goes blank because a single feed is slow or
 * refused is worse than a homepage with two feeds and an explanation — so
 * every state here is scoped to one section and none of them can take the page
 * with them.
 */
export function FeedSection({
  title,
  description,
  totalCount,
  countNoun,
  shownCount,
  fetching,
  error,
  failureSummary,
  emptyMessage,
  onShowMore,
  children,
}: FeedSectionProps) {
  const showingSome = totalCount !== undefined && shownCount < totalCount
  // FIRST LOAD ONLY. urql keeps `data` across a refetch while `fetching` goes
  // true, so a bare `fetching` check would throw the rows away every time
  // "show more" ran — the list would blink out from under the button that was
  // just pressed.
  const loadingFirstTime = fetching && shownCount === 0 && !error

  return (
    <Paper component="section" variant="outlined" aria-labelledby={headingId(title)} sx={{ p: 2 }}>
      <Typography id={headingId(title)} variant="h6" component="h2">
        {title}
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
        {description}
      </Typography>

      {/* Mounted WITH its content on first paint, which is deliberately not
          announced — three feeds announcing their counts at once on page load
          is chatter nobody asked for. Updating it after "show more", which the
          reader did ask for, is announced, because that is a change inside a
          region that already existed. */}
      {!error && totalCount !== undefined && countNoun !== undefined && (
        <Typography variant="body2" color="text.secondary" aria-live="polite" sx={{ mt: 1 }}>
          {totalCount.toLocaleString()} {countNoun}
          {totalCount === 1 ? '' : 's'}
          {showingSome ? ` — showing ${shownCount.toLocaleString()}` : ''}
        </Typography>
      )}

      {loadingFirstTime && (
        <Stack spacing={1} sx={{ mt: 1 }}>
          <Skeleton variant="rectangular" height={44} />
          <Skeleton variant="rectangular" height={44} />
          <Skeleton variant="rectangular" height={44} />
        </Stack>
      )}

      {/* Inline and scoped to this section — the other two feeds carry on. */}
      {error && (
        <Alert severity="info" sx={{ mt: 1 }}>
          {failureSummary}
        </Alert>
      )}

      {!error && !fetching && shownCount === 0 && (
        <Typography color="text.secondary" sx={{ mt: 1 }}>
          {emptyMessage}
        </Typography>
      )}

      {!error && shownCount > 0 && <List disablePadding>{children}</List>}

      {!error && onShowMore && (
        <Button size="small" onClick={onShowMore} disabled={fetching} sx={{ mt: 1 }}>
          {fetching ? 'Loading…' : 'Show more'}
        </Button>
      )}
    </Paper>
  )
}

/** A stable id per section, so the Paper's `aria-labelledby` names its own heading. */
function headingId(title: string): string {
  return `feed-${title.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`
}
