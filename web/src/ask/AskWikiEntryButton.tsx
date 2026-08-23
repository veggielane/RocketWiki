import { IconButton, Tooltip } from '@mui/material'
import QuestionAnswerOutlinedIcon from '@mui/icons-material/QuestionAnswerOutlined'
import { Link as RouterLink } from 'react-router-dom'
import { useAskWikiPossiblyAvailable } from './askAvailability'

/**
 * App-bar entry point to /ask, placed right next to the search box — the
 * two "find something" affordances live together. Rendered only while the
 * session hasn't learned the assistant is NOT_CONFIGURED
 * (askAvailability.ts — the first ask is the probe; there is no status
 * query to gate on), mirroring the GitLab treatment of absent features.
 */
export function AskWikiEntryButton() {
  const possiblyAvailable = useAskWikiPossiblyAvailable()
  if (!possiblyAvailable) return null
  return (
    <Tooltip title="Ask the wiki">
      <IconButton component={RouterLink} to="/ask" aria-label="Ask the wiki">
        <QuestionAnswerOutlinedIcon />
      </IconButton>
    </Tooltip>
  )
}
