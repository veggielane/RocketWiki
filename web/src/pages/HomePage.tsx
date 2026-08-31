import { useState } from 'react'
import { Link as RouterLink } from 'react-router-dom'
import { Link, Stack, Typography } from '@mui/material'
import {
  useActivityFeedQuery,
  useMyRecentlyViewedQuery,
  useMyStaleContentQuery,
} from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { FeedSection } from '../home/FeedSection'
import { FeedPageRow, InSpace } from '../home/FeedPageRow'
import { UserAvatar } from '../avatars/UserAvatar'
import { describeTimeSince } from '../format/relativeTime'
import { FEED_PAGE_SIZE, FEED_MAX_PAGE_SIZE } from '../home/feedPaging'

/**
 * What you land on: what has changed, what you have let go stale, and where you
 * were.
 *
 * **Three queries, never one.** A single query would mean one slow or refused
 * feed blanks the whole homepage; three give each section its own skeleton,
 * error and empty state, and a homepage that is still worth having when one of
 * them is not working.
 *
 * Every feed is permission-filtered server-side and pre-filtered at the item
 * level, so there is no "you cannot see this one" row to render around — a page
 * you may not view simply never enters the connection (§6.7). Each row still
 * carries its own marking badge, because these are cross-space reads and a row
 * can name something far more sensitive than anything else on screen.
 *
 * "Show more" is LOCAL state, deliberately unlike search and the user roster,
 * which put their page size in the URL. Those are shareable: someone sends a
 * link to a scrolled search or a filtered roster. Nobody sends a link to their
 * own expanded recently-viewed, and three feeds would put three parameters on
 * `/` to support it. If that inconsistency looks like an oversight later, it is
 * not.
 */
export function HomePage() {
  useDocumentTitle('Home')
  const [activityShown, setActivityShown] = useState(FEED_PAGE_SIZE)
  const [staleShown, setStaleShown] = useState(FEED_PAGE_SIZE)
  const [viewedShown, setViewedShown] = useState(FEED_PAGE_SIZE)

  const [activity] = useActivityFeedQuery({ variables: { first: activityShown } })
  const [stale] = useMyStaleContentQuery({ variables: { first: staleShown } })
  const [viewed] = useMyRecentlyViewedQuery({ variables: { first: viewedShown } })

  const activityRows = activity.data?.activityFeed?.nodes ?? []
  const staleRows = stale.data?.myStaleContent?.nodes ?? []
  const viewedRows = viewed.data?.myRecentlyViewed?.nodes ?? []

  return (
    <Stack spacing={3}>
      <PageHeader
        title="Home"
        description="What has changed lately, what you have left untouched, and where you have been. Everything here is filtered to pages you can view."
      />

      <FeedSection
        title="Recent activity"
        description="Pages created and edited across every space you can see, newest first."
        totalCount={activity.data?.activityFeed?.totalCount}
        countNoun="change"
        shownCount={activityRows.length}
        fetching={activity.fetching}
        error={activity.error !== undefined}
        failureSummary={describeLoadFailure('ACTIVITY_FEED').summary}
        emptyMessage={
          <>
            Nothing has been created or edited yet in any space you can see.{' '}
            <Link component={RouterLink} to="/spaces">
              Browse the spaces
            </Link>{' '}
            to make the first change.
          </>
        }
        onShowMore={
          activity.data?.activityFeed?.pageInfo.hasNextPage && activityShown < FEED_MAX_PAGE_SIZE
            ? () => setActivityShown((shown) => Math.min(shown + FEED_PAGE_SIZE, FEED_MAX_PAGE_SIZE))
            : undefined
        }
      >
        {activityRows.map((item) => (
          <FeedPageRow
            key={`${item.page.id}-${item.revisionNumber}`}
            page={item.page}
            detail={
              <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
                <UserAvatar
                  userId={item.author.id}
                  hasAvatar={item.author.hasAvatar}
                  displayName={item.author.displayName}
                  size={18}
                />
                <Typography component="span" variant="caption" color="text.secondary">
                  {/* Created or edited, said in words rather than by an icon:
                      the difference between a new page and a change to an old
                      one is the most useful thing in the row. */}
                  {item.author.displayName} {item.isCreate ? 'created this' : 'edited this'}{' '}
                  {describeTimeSince(item.occurredAtUtc)}
                </Typography>
                <InSpace spaceKey={item.page.spaceKey} />
              </Stack>
            }
          />
        ))}
      </FeedSection>

      <FeedSection
        title="Stale content you own"
        description="Pages you created or edited that nobody has touched in a while, longest-untouched first. Ordered by anyone's last edit, not your own — a page someone else has since maintained is not neglected."
        totalCount={stale.data?.myStaleContent?.totalCount}
        countNoun="page"
        shownCount={staleRows.length}
        fetching={stale.fetching}
        error={stale.error !== undefined}
        failureSummary={describeLoadFailure('STALE_CONTENT').summary}
        emptyMessage="Nothing of yours has gone stale — either everything you have written is being kept up to date, or you have not created or edited a page yet."
        onShowMore={
          stale.data?.myStaleContent?.pageInfo.hasNextPage && staleShown < FEED_MAX_PAGE_SIZE
            ? () => setStaleShown((shown) => Math.min(shown + FEED_PAGE_SIZE, FEED_MAX_PAGE_SIZE))
            : undefined
        }
      >
        {staleRows.map((item) => (
          <FeedPageRow
            key={item.page.id}
            page={item.page}
            detail={
              <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
                <Typography component="span" variant="caption" color="text.secondary">
                  {/* The value the server SORTED by. Showing any other
                      timestamp here would make a correctly-ordered list look
                      shuffled. */}
                  last updated {describeTimeSince(item.page.updatedAtUtc)}
                </Typography>
                <InSpace spaceKey={item.page.spaceKey} />
              </Stack>
            }
          />
        ))}
      </FeedSection>

      <FeedSection
        title="Recently viewed"
        description="Pages you opened, most recent first."
        shownCount={viewedRows.length}
        fetching={viewed.fetching}
        error={viewed.error !== undefined}
        failureSummary={describeLoadFailure('RECENTLY_VIEWED').summary}
        emptyMessage="Pages you open will appear here, so you can get back to them."
        onShowMore={
          viewed.data?.myRecentlyViewed?.pageInfo.hasNextPage && viewedShown < FEED_MAX_PAGE_SIZE
            ? () => setViewedShown((shown) => Math.min(shown + FEED_PAGE_SIZE, FEED_MAX_PAGE_SIZE))
            : undefined
        }
      >
        {viewedRows.map((item) => (
          <FeedPageRow
            key={item.page.id}
            page={item.page}
            detail={
              <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
                <Typography component="span" variant="caption" color="text.secondary">
                  viewed {describeTimeSince(item.lastViewedAtUtc)}
                </Typography>
                <InSpace spaceKey={item.page.spaceKey} />
              </Stack>
            }
          />
        ))}
      </FeedSection>
    </Stack>
  )
}
