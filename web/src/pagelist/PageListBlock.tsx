import { usePageListQueryQuery } from '../graphql/generated/graphql'
import type { PageListSpec } from './fenceBody'
import { AggregateMarkingBanner } from '../markings/AggregateMarkingBanner'
import { MarkingLevelBadge } from '../markings/MarkingLevelBadge'
import { describePageListUnavailable } from '../feedback/unavailableCopy'
import { offendingText } from './rqlErrorSpans'

/**
 * Live rendering for a ` ```page-list ` fence (design.md §22): the pages
 * matching the fence's RQL query, each with its space and classification
 * badge, under the aggregate marking for the whole match set.
 *
 * Plain CSS, not MUI — editor-content territory, like the GitLab and diagram
 * blocks. The two marking components are the exception on purpose: §21 puts
 * ONE renderer behind each marking surface, and a hand-rolled badge here
 * would be a second one.
 *
 * **§6.7, which shapes everything below.** Pages the viewer cannot see are
 * absent from `edges` and uncounted by `totalCount` — they were filtered out
 * per page, server-side, before this payload existed. So there is no "some
 * results hidden" affordance, no count other than the server's own, and no
 * gap in the list to explain. `totalCount` is safe to show precisely because
 * it counts visible results only (§22.5); the truncation line below talks
 * about the fence's `limit` and nothing else.
 */
export function PageListBlock({ spec }: { spec: PageListSpec }) {
  const [{ data, error }] = usePageListQueryQuery({
    variables: { query: spec.query, first: spec.limit ?? null },
    requestPolicy: 'cache-and-network',
  })

  const payload = data?.pageQuery

  if (error) {
    return <PageListPlaceholder reason="REQUEST_FAILED" query={spec.query} />
  }

  if (!payload) {
    return (
      <div className="rw-page-list-placeholder" role="note">
        <div className="rw-diagram-hint">Loading matching pages…</div>
        <code className="rw-page-list-query">{spec.query}</code>
      </div>
    )
  }

  // §22.6: an unparseable query is authored content, so it comes back
  // positioned in the payload rather than as a GraphQL error, and the author
  // gets to see what is wrong and where. Showing the query back discloses
  // nothing new — it is already this page's own Markdown.
  if (payload.errors.length > 0) {
    return (
      <PageListPlaceholder reason="QUERY_INVALID" query={spec.query}>
        <ul className="rw-page-list-errors">
          {payload.errors.map((rqlError, i) => {
            const offending = offendingText(spec.query, rqlError)
            return (
              <li key={`${rqlError.code}-${rqlError.offset}-${i}`}>
                {rqlError.message}
                {offending.length > 0 && (
                  <>
                    {' '}
                    <code className="rw-page-list-offending">{offending}</code>
                  </>
                )}
              </li>
            )
          })}
        </ul>
      </PageListPlaceholder>
    )
  }

  const rows = payload.edges
  const total = payload.totalCount

  if (rows.length === 0) {
    return (
      <div className="rw-page-list">
        <div className="rw-page-list-header">
          {/* Fact and consequence, and deliberately incurious about why. An
              empty result and a result set filtered down to empty are the
              same sentence here, which is §6.7's whole point. */}
          <span>No pages match this query, so this list is empty.</span>
        </div>
      </div>
    )
  }

  return (
    <div className="rw-page-list">
      {/* §21.13. Rendered above the rows because a marking has to be met
          before the thing it marks is read. The scope note (placement
          `page-list`) says the label covers the whole match set — which the
          `limit` below may well have cut short — so a label out-ranking every
          visible badge reads as truthful rather than wrong. */}
      <AggregateMarkingBanner marking={payload.aggregateMarking} placement="page-list" />
      <div className="rw-page-list-header">
        <span>
          {rows.length < total
            ? `Showing the first ${rows.length} of ${total} matching page${total === 1 ? '' : 's'}.`
            : `${total} matching page${total === 1 ? '' : 's'}.`}
        </span>
      </div>
      {/* A real list, not a pile of divs (WCAG 1.3.1): a screen reader is told
          how many pages there are and where it is in them. A table was the
          other option and lost on the classification column — MarkingLevelBadge
          already carries its own visually-hidden "Classification:" lead-in for
          exactly this kind of surface, and a column header would say it twice. */}
      <ul className="rw-page-list-rows">
        {rows.map(({ cursor, node }) => (
          <li key={cursor}>
            {/* A real route href, not a page:// scheme string: this is rendered
                UI, not stored Markdown, and the two are not interchangeable. */}
            <a className="rw-page-list-link" href={`/pages/${node.page.id}`}>
              {node.page.title}
            </a>
            <span className="rw-page-list-space">
              <span className="rw-visually-hidden">in space </span>
              {node.page.spaceKey}
            </span>
            {/* §21: the level only. Rows are already permission-filtered, so
                this gates nothing — it tells a reader scanning the list how
                sensitive each page is before they open it. The page's own
                banner carries the full marking, caveat included. */}
            <MarkingLevelBadge level={node.page.marking.level} levelName={node.page.marking.levelName} />
          </li>
        ))}
      </ul>
    </div>
  )
}

/**
 * The degraded frame: one sentence from the shared vocabulary, the query it
 * is about, and optionally the detail. `role="note"` rather than `role="alert"`
 * on purpose — these render with the page rather than in response to anything
 * the reader did, and a page carrying several broken widgets would otherwise
 * fire an alert per widget on load.
 */
function PageListPlaceholder({
  reason,
  query,
  children,
}: {
  reason: 'REQUEST_FAILED' | 'QUERY_INVALID'
  query: string
  children?: React.ReactNode
}) {
  return (
    <div className="rw-page-list-placeholder" role="note">
      <div className="rw-page-list-reason">{describePageListUnavailable(reason).summary}</div>
      <code className="rw-page-list-query">{query}</code>
      {children}
    </div>
  )
}
