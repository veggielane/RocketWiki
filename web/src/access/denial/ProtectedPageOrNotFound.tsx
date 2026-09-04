import { Alert } from '@mui/material'
import { describeLoadFailure } from '../../feedback/unavailableCopy'
import { ProtectedPageScreen, type ProtectedPageDenial } from './ProtectedPageScreen'

export interface ProtectedPageOrNotFoundProps {
  /**
   * The `denial` half of a `PageAccessResult`. Null or undefined means the
   * whole result was null — no such page — which is the not-found notice.
   */
  denial: ProtectedPageDenial | null | undefined
}

/**
 * What a page route renders when the disclosing read did not hand back a
 * page (design.md §6.7 / §21.8): the protected screen for a denial, the
 * ordinary "couldn't load" notice for nothing at all. The route already ran
 * `pageAccess`, so there is no second query here — the API answers
 * page-or-denial in one request, one audit row, and every sentence the
 * screen puts beside a gate comes from the denial itself. Nothing about the
 * reader is fetched to say what they hold: no gate on the ladder is about a
 * level, so there is no "you hold X" to add.
 */
export function ProtectedPageOrNotFound({ denial }: ProtectedPageOrNotFoundProps) {
  if (!denial) {
    return <Alert severity="info">{describeLoadFailure('PAGE').summary}</Alert>
  }
  return <ProtectedPageScreen denial={denial} />
}
