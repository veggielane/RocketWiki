import { Link, Typography } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import { useAskWikiPossiblyAvailable } from './askAvailability'

/**
 * "Can't find it? Ask the wiki" — shown under search results, prefilling
 * the Ask page with the search query (`/ask?q=…`; prefill only, the user
 * still submits — an ask is multi-second and shouldn't fire as a side
 * effect of navigation). Hidden for the session once any ask has answered
 * NOT_CONFIGURED (askAvailability.ts).
 */
export function AskWikiSearchNudge({ query }: { query: string }) {
  const possiblyAvailable = useAskWikiPossiblyAvailable()
  if (!possiblyAvailable) return null
  return (
    <Typography variant="body2" color="text.secondary">
      Can't find it?{' '}
      <Link component={RouterLink} to={`/ask?q=${encodeURIComponent(query)}`}>
        Ask the wiki
      </Link>
    </Typography>
  )
}
