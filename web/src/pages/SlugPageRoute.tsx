import { useParams } from 'react-router-dom'
import { Alert, Skeleton } from '@mui/material'
import { usePageBySlugQuery } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { PageViewPage } from './PageViewPage'

/**
 * `/spaces/{spaceKey}/{slug}` — the readable address for a page.
 *
 * Slugs are unique per space and the hierarchy is deliberately NOT in the URL, so
 * moving a page around the tree never changes its address or breaks a link to it.
 * `/pages/{id}` still works and always will: it is what search results, page-list
 * widgets and anything holding only an id can link to, and it is the address that
 * survives even a slug being changed.
 *
 * This resolves the slug to an id and then renders the ordinary page view, rather
 * than being a second page-rendering path that could drift from the first. The
 * resolution is one tiny query; the page itself is read exactly as the id route
 * reads it.
 *
 * A missing page and one this caller may not view are the same "not found" here,
 * because the server made them the same (design.md §6.7 — invisible must be
 * indistinguishable from absent, or the URL becomes a way to ask whether a page
 * exists).
 */
export function SlugPageRoute() {
  const { spaceKey, slug } = useParams<{ spaceKey: string; slug: string }>()
  const [{ data, fetching, error }] = usePageBySlugQuery({
    variables: { spaceKey: spaceKey ?? '', slug: slug ?? '' },
    pause: !spaceKey || !slug,
  })

  if (fetching) {
    return <Skeleton variant="rectangular" height={300} />
  }

  const pageId = data?.pageBySlug?.id
  if (error || !pageId) {
    return <Alert severity="info">{describeLoadFailure('PAGE').summary}</Alert>
  }

  return <PageViewPage pageId={pageId} />
}
