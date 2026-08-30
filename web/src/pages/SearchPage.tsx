import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link as RouterLink, useSearchParams } from 'react-router-dom'
import {
  Alert,
  Autocomplete,
  Box,
  Breadcrumbs,
  Button,
  List,
  ListItem,
  ListItemButton,
  ListItemText,
  Skeleton,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { useSearchFacetsQuery, useSearchPagesQuery, type SearchPagesQuery } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { useDebouncedValue } from '../useDebouncedValue'
import { MarkingLevelBadge } from '../markings/MarkingLevelBadge'
import { AggregateMarkingBanner } from '../markings/AggregateMarkingBanner'
import { AskWikiSearchNudge } from '../ask/AskWikiSearchNudge'

type SearchEdge = SearchPagesQuery['search']['edges'][number]

/** Long enough that a typed word is one request, short enough to feel live. */
const SEARCH_DEBOUNCE_MS = 300

/**
 * design.md §9: hybrid keyword + semantic ranking is entirely a server
 * concern — this just renders whatever order the server returns. Each
 * result deep-links straight to its matching section via `anchorId`
 * (heading-path-derived — the server ports editor/headingAnchors.ts's
 * algorithm exactly, verified against the shared corpus).
 */
export function SearchPage() {
  const [params, setParams] = useSearchParams()
  const query = params.get('q') ?? ''
  useDocumentTitle(query ? `Search — ${query}` : 'Search')

  // THE WHOLE SEARCH LIVES IN THE URL. Query, space and labels were split
  // between the URL and component state, so refreshing, bookmarking, sharing or
  // pressing Back after opening a result all silently dropped the filters and
  // returned a different result set than the one that was clicked from.
  const spaceKey = params.get('space')
  const labels = useMemo(() => params.getAll('label'), [params])

  // What is typed, versus what has been committed to the URL. The field is not
  // driven by the URL, so typing stays instant; the debounced value is what
  // becomes a request.
  const [draft, setDraft] = useState(query)
  const debouncedDraft = useDebouncedValue(draft, SEARCH_DEBOUNCE_MS)
  const [after, setAfter] = useState<string | undefined>(undefined)
  const [edges, setEdges] = useState<SearchEdge[]>([])

  /**
   * The last `q` THIS component wrote. It is what tells "the debounce settled,
   * push it to the URL" apart from "someone else changed the URL, adopt it" —
   * a distinction no comparison of the two values can make, and whose absence
   * broke the header's search box.
   *
   * The old pair of effects compared `debouncedDraft` against `query` in both
   * directions. Submitting `b` from the header while the page sat on `?q=a`
   * therefore ran, in order: effect A saw a stale `debouncedDraft` of `'a'`,
   * decided the URL was wrong, and rewrote it BACK to `?q=a` over the
   * navigation that had just happened; effect B then pulled `draft` back to
   * `'a'` too, cancelling the pending timer. The typed query was silently
   * discarded — as was every Back/Forward between two searches.
   */
  const lastWrittenQuery = useRef(query)

  /** The current params with one search field replaced — everything else survives. */
  const withParam = useCallback(
    (mutate: (next: URLSearchParams) => void) => {
      const next = new URLSearchParams(params)
      mutate(next)
      return next
    },
    [params],
  )

  // Settled draft → URL.
  useEffect(() => {
    // Still settling: the user is mid-word.
    if (debouncedDraft !== draft) return
    // The URL moved under us (header submit, Back/Forward). Stand down and let
    // the adopt-effect below take it; writing here is what clobbered it.
    if (query !== lastWrittenQuery.current) return
    if (debouncedDraft === query) return
    lastWrittenQuery.current = debouncedDraft
    // `replace` so a search is ONE history entry rather than one per keystroke.
    setParams(
      withParam((next) => {
        if (debouncedDraft) next.set('q', debouncedDraft)
        else next.delete('q')
      }),
      { replace: true },
    )
  }, [debouncedDraft, draft, query, setParams, withParam])

  // URL → field, when the change came from anywhere but us.
  useEffect(() => {
    if (query === lastWrittenQuery.current) return
    lastWrittenQuery.current = query
    setDraft(query)
  }, [query])

  const setSpaceKey = (value: string | null) =>
    setParams(
      withParam((next) => {
        if (value) next.set('space', value)
        else next.delete('space')
      }),
      { replace: true },
    )

  const setLabels = (values: string[]) =>
    setParams(
      withParam((next) => {
        next.delete('label')
        for (const label of values) next.append('label', label)
      }),
      { replace: true },
    )

  const clearFilters = () =>
    setParams(
      withParam((next) => {
        next.delete('space')
        next.delete('label')
      }),
      { replace: true },
    )

  const hasFilters = spaceKey !== null || labels.length > 0

  // A genuinely new search (query/space/labels changed) starts pagination
  // over — otherwise "Load more" would keep paging through stale results
  // for the previous query. Keyed on the joined labels rather than the array,
  // which `getAll` rebuilds on every render. JSON rather than a join, so a
  // label containing the separator cannot collide with two labels.
  const labelKey = JSON.stringify(labels)
  useEffect(() => {
    setAfter(undefined)
    setEdges([])
  }, [query, spaceKey, labelKey])

  const [{ data: facetData }] = useSearchFacetsQuery({ variables: { spaceKey: spaceKey ?? undefined } })
  const [{ data, fetching, error }] = useSearchPagesQuery({
    variables: { query, spaceKey: spaceKey ?? undefined, labels: labels.length > 0 ? labels : undefined, after },
    pause: query.trim().length === 0,
  })

  useEffect(() => {
    if (!data) return
    setEdges((prev) => (after ? [...prev, ...data.search.edges] : data.search.edges))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data])

  return (
    <Stack spacing={2}>
      <PageHeader title="Search" />

      <TextField
        label="Query"
        value={draft}
        // `replace`, not the default push. Every keystroke used to add a history
        // entry, so pressing Back after typing "ignition" walked backwards
        // through i-g-n-i-t-i-o-n. The debounce (useDebouncedValue) is what
        // stops one request per character.
        onChange={(e) => setDraft(e.target.value)}
        fullWidth
      />

      <Stack direction="row" spacing={2}>
        <Autocomplete
          size="small"
          options={facetData?.spaces.map((s) => s.key) ?? []}
          getOptionLabel={(key) => facetData?.spaces.find((s) => s.key === key)?.name ?? key}
          value={spaceKey}
          onChange={(_e, value) => setSpaceKey(value)}
          renderInput={(params) => <TextField {...params} label="Space" />}
          sx={{ minWidth: 200 }}
        />
        <Autocomplete
          multiple
          size="small"
          options={facetData?.labels ?? []}
          value={labels}
          onChange={(_e, value) => setLabels(value)}
          renderInput={(params) => <TextField {...params} label="Labels" />}
          sx={{ minWidth: 240 }}
        />
        {/* Only when there is something to clear. A filter that eliminated
            every result is otherwise a dead end: the empty state below says so,
            and this is the way out of it. */}
        {hasFilters && (
          <Button size="small" onClick={clearFilters} sx={{ alignSelf: 'center', flexShrink: 0 }}>
            Clear filters
          </Button>
        )}
      </Stack>

      {query.trim().length === 0 && <Typography color="text.secondary">Type a query to search.</Typography>}

      {fetching && edges.length === 0 && (
        <Stack spacing={1}>
          <Skeleton variant="rectangular" height={80} />
          <Skeleton variant="rectangular" height={80} />
        </Stack>
      )}

      {error && <Alert severity="info">{describeLoadFailure('SEARCH').summary}</Alert>}

      {data && (
        <>
          {/* A live region, so the result count reaches someone who is still in
              the query field and cannot see the list change. The ask page
              already does this for its answers; search did not. */}
          <Typography variant="body2" color="text.secondary" role="status" aria-live="polite">
            {data.search.totalCount} result{data.search.totalCount === 1 ? '' : 's'}
          </Typography>
          {/* design.md §21.13: a result list is a compilation and carries the
              classification of its most sensitive constituent. The aggregate
              spans the whole permission-filtered hit set — the same set
              `totalCount` counts, not the edges currently rendered — so it can
              out-rank every badge on screen and does not drop as you page.
              The banner's scope note says so; without it the label would read
              as a claim about the visible rows. An empty result set has no
              aggregate and gets no banner. */}
          <AggregateMarkingBanner marking={data.search.aggregateMarking} placement="results" />
          <List disablePadding>
            {edges.map(({ node, cursor }) => (
              // ListItem (an <li>) wraps the link — a bare <a> as a direct
              // <ul> child is invalid list markup (WCAG 1.3.1 / axe "list").
              <ListItem key={cursor} disablePadding>
                <ListItemButton
                  component={RouterLink}
                  to={`/pages/${node.page.id}#${node.anchorId}`}
                  sx={{ display: 'block', py: 1.5 }}
                >
                  {node.headingPath.length > 0 && (
                    <Breadcrumbs separator="›" sx={{ fontSize: '0.75rem', mb: 0.5 }}>
                      <Typography variant="caption" color="text.secondary">
                        {node.page.title}
                      </Typography>
                      {node.headingPath.map((heading, i) => (
                        <Typography key={i} variant="caption" color="text.secondary">
                          {heading}
                        </Typography>
                      ))}
                    </Breadcrumbs>
                  )}
                  {/* design.md §21: results are already clearance-filtered
                      server-side, so the badge is not a gate — it tells a
                      reader scanning a list how sensitive each hit is before
                      they open it. The level only; the page's own banners
                      carry the full marking. */}
                  <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
                    <ListItemText
                      primary={
                        node.headingPath.length > 0 ? node.headingPath[node.headingPath.length - 1] : node.page.title
                      }
                      secondary={node.snippet}
                    />
                    <Box sx={{ pt: 0.5 }}>
                      <MarkingLevelBadge
                        level={node.page.marking.level}
                        levelName={node.page.marking.levelName}
                      />
                    </Box>
                  </Stack>
                </ListItemButton>
              </ListItem>
            ))}
          </List>
          {edges.length === 0 && (
            <Typography color="text.secondary">
              {/* Names the filters when there are some. "Check the spelling" is
                  the wrong advice when a space or label is what eliminated
                  everything, and it left the reader with no idea a filter was
                  even applied. */}
              {hasFilters
                ? `No results for "${query}" with the current filters — clear them, or try different words.`
                : `No results for "${query}" — check the spelling or try different words.`}
            </Typography>
          )}
          {/* Prefills /ask with this query; hides itself for the session
              once the assistant is known NOT_CONFIGURED. */}
          <AskWikiSearchNudge query={query} />
          {data.search.pageInfo.hasNextPage && (
            <Button
              variant="outlined"
              size="small"
              disabled={fetching}
              onClick={() => setAfter(data.search.pageInfo.endCursor ?? undefined)}
              sx={{ alignSelf: 'flex-start' }}
            >
              {fetching ? 'Loading…' : 'Load more'}
            </Button>
          )}
        </>
      )}
    </Stack>
  )
}
