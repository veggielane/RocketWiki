import { useEffect, useState } from 'react'
import { Link as RouterLink, useSearchParams } from 'react-router-dom'
import {
  Alert,
  Autocomplete,
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
import { AskWikiSearchNudge } from '../ask/AskWikiSearchNudge'

type SearchEdge = SearchPagesQuery['search']['edges'][number]

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
  const [spaceKey, setSpaceKey] = useState<string | null>(null)
  const [labels, setLabels] = useState<string[]>([])
  const [after, setAfter] = useState<string | undefined>(undefined)
  const [edges, setEdges] = useState<SearchEdge[]>([])

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
      <Typography variant="h4" component="h1">
        Search
      </Typography>

      <TextField
        label="Query"
        value={query}
        onChange={(e) => setParams(e.target.value ? { q: e.target.value } : {})}
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

      {error && <Alert severity="info">Couldn't search — there's no live API in this environment yet.</Alert>}

      {data && (
        <>
          <Typography variant="body2" color="text.secondary">
            {data.search.totalCount} result{data.search.totalCount === 1 ? '' : 's'}
          </Typography>
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
                  <ListItemText
                    primary={node.headingPath.length > 0 ? node.headingPath[node.headingPath.length - 1] : node.page.title}
                    secondary={node.snippet}
                  />
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
