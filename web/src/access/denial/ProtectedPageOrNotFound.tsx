import { Alert } from '@mui/material'
import { useClassificationSchemeQuery, useCurrentUserQuery } from '../../graphql/generated/graphql'
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
 * `pageAccess`, so there is no second query here for the denial itself —
 * the API answers page-or-denial in one request, one audit row.
 *
 * What IS fetched, and only for a denial that names the clearance gate: the
 * caller's own clearance and the scheme's spelling of it, so the sentence
 * can say "you hold OFFICIAL-SENSITIVE" rather than only what is needed.
 * Both reads are affordance data the caller is entitled to; neither is
 * about the withheld page.
 */
export function ProtectedPageOrNotFound({ denial }: ProtectedPageOrNotFoundProps) {
  if (!denial) {
    return <Alert severity="info">{describeLoadFailure('PAGE').summary}</Alert>
  }
  return <ConnectedProtectedPage denial={denial} />
}

function ConnectedProtectedPage({ denial }: { denial: ProtectedPageDenial }) {
  const namesClearance = !denial.noSpaceAccess && denial.reasons.some((reason) => reason.gate === 'CLASSIFICATION')
  const [{ data: meData }] = useCurrentUserQuery({ pause: !namesClearance })
  const [{ data: schemeData }] = useClassificationSchemeQuery({ pause: !namesClearance })
  const heldLevel = meData?.me.clearance
  const heldLevelName =
    heldLevel !== undefined
      ? (schemeData?.classificationScheme.find((entry) => entry.level === heldLevel)?.name ?? null)
      : null
  return <ProtectedPageScreen denial={denial} heldLevelName={heldLevelName} />
}
