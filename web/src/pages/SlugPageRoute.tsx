import { useParams } from 'react-router-dom'
import { Alert, Skeleton } from '@mui/material'
import { usePageAccessBySlugQuery } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { ProtectedPageOrNotFound } from '../access/denial/ProtectedPageOrNotFound'
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
 * Three answers, not two (design.md §6.7 / §21.8). A page this caller may not
 * read comes back as a DENIAL — its marking and every failing gate, never its
 * title — and renders as the protected screen right here, before any id
 * exists to hand on. A slug nobody has comes back null and is the not-found
 * notice. The two are told apart on purpose: a reader inside a space is owed
 * an honest answer about what sits at an address, and the audit row is the
 * same either way.
 */
export function SlugPageRoute() {
  const { spaceKey, slug } = useParams<{ spaceKey: string; slug: string }>()
  const [{ data, fetching, error }] = usePageAccessBySlugQuery({
    variables: { spaceKey: spaceKey ?? '', slug: slug ?? '' },
    pause: !spaceKey || !slug,
  })

  // FIRST LOAD ONLY. urql retains `data` across a refetch and flips `fetching`
  // true (urql.js computeNextState), so a bare `if (fetching)` threw the screen
  // away on every post-write refetch: content, scroll position and keyboard
  // focus all went with it. `&& !data` keeps the rendered screen up while the
  // re-read happens underneath it.
  if (fetching && !data) {
    return <Skeleton variant="rectangular" height={300} />
  }

  if (error) {
    return <Alert severity="info">{describeLoadFailure('PAGE_ACCESS').summary}</Alert>
  }

  const access = data?.pageAccessBySlug
  const pageId = access?.page?.id
  if (!pageId) {
    return <ProtectedPageOrNotFound denial={access?.denial} />
  }

  return <PageViewPage pageId={pageId} />
}
