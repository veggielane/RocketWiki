import { useParams } from 'react-router-dom'
import { Skeleton } from '@mui/material'
import { useSpaceTreeQuery } from '../graphql/generated/graphql'
import { SpaceBrowserPage } from './SpaceBrowserPage'
import { PageViewPage } from './PageViewPage'

/**
 * `/spaces/{key}` — the space's default page when it has one, its browser when
 * it does not.
 *
 * A default page nothing defaults to would not be a feature, so this is where
 * the setting earns its name. The browser has not gone anywhere: it stays at
 * `/spaces/{key}/-/browse`, under the same reserved segment every other
 * space-level screen uses, and it is what a space without a homepage still
 * shows here.
 *
 * Renders the ordinary page view rather than reimplementing one, for the same
 * reason SlugPageRoute does: two page-rendering paths would drift, and this way
 * the homepage gets the real thing — comments, attachments, marking banners and
 * all — without a second code path to keep honest.
 *
 * A homepage the caller may not view falls back to the browser rather than a
 * refusal. The server already collapses "denied" into "absent" (design.md
 * §6.7), so a space whose default page is above your clearance simply behaves
 * like a space without one — which reveals nothing, and beats a dead end where
 * the space used to be.
 */
export function SpaceHomeRoute() {
  const { spaceKey } = useParams<{ spaceKey: string }>()
  const [{ data, fetching }] = useSpaceTreeQuery({
    variables: { key: spaceKey ?? '' },
    pause: !spaceKey,
  })

  if (fetching) {
    return <Skeleton variant="rectangular" height={300} />
  }

  const homepageId = data?.space?.homepageId
  if (!homepageId) {
    return <SpaceBrowserPage />
  }

  return <PageViewPage pageId={homepageId} onUnavailable={<SpaceBrowserPage />} />
}
