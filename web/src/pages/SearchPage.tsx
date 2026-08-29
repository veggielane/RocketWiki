import { useEffect, useState } from 'react'
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

  // What is typed, versus what has been committed to the URL. The field is
  // uncontrolled by the URL so typing stays instant; the debounced value is
  // what becomes a history entry and a request.
  const [draft, setDraft] = useState(query)
  const debouncedDraft = useDebouncedValue(draft, SEARCH_DEBOUNCE_MS)
  const [spaceKey, setSpaceKey] = useState<string | null>(null)
  const [labels, setLabels] = useState<string[]>([])
  const [after, setAfter] = useState<string | undefined>(undefined)
  const [edges, setEdges] = useState<SearchEdge[]>([])

  // The settled draft becomes the URL. `replace` so a search is ONE history
  // entry rather than one per keystroke, and only when it actually differs —
  // otherwise arriving with `?q=` from the header would immediately rewrite it.
  useEffect(() => {
    if (debouncedDraft === query) return
    setParams(debouncedDraft ? { q: debouncedDraft } : {}, { replace: true })
  }, [debouncedDraft, query, setParams])

  // Someone else changed the query — the header's search box, or a back/forward
  // that landed on a different one. The field follows the URL in that direction
  // too, or it would keep showing what was typed here.
  useEffect(() => {
    setDraft((current) => (current === query ? current : query))
  }, [query])

  // A genuinely new search (query/space/labels changed) starts pagination
  // over — otherwise "Load more" would keep paging through stale results
  // for the previous query.
  useEffect(() => {
    setAfter(undefined)
    setEdges([])
  }, [query, spaceKey, labels])

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
              No results for "{query}" — check the spelling or try different words.
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
