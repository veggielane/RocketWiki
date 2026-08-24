import type { MutationErrorFragment } from './generated/graphql'

/**
 * The API flattens all typed mutation errors into one PageMutationErrorView
 * with a `kind` discriminator (design.md §8: "every mutation returns a
 * payload type with typed errors"); only the fields relevant to a kind are
 * non-null. These helpers are the one place that string is interpreted —
 * components branch on the narrowed results, never on raw `kind` strings,
 * so a renamed kind breaks one module (and its test), not every call site.
 *
 * The designed failure modes are UX, not exceptions: StaleRevision opens
 * the merge flow, ReadOnlyReplica opens the replica explainer. Everything
 * else gets `describeMutationError`'s plain-language fallback rather than
 * a raw error toast.
 */

export interface StaleRevision {
  expectedRevisionNumber: number | null
  actualRevisionNumber: number | null
  latestTitle: string | null
  latestContent: string | null
}

export interface ReadOnlyReplica {
  spaceId: string | null
  /** design.md §12: the origin instance, so the UI can say "Replica of <origin> — read-only" rather than a bare refusal. */
  originInstanceId: string | null
}

export function asStaleRevision(error: MutationErrorFragment | null | undefined): StaleRevision | null {
  if (error?.kind !== 'StaleRevision') return null
  return {
    expectedRevisionNumber: error.expectedRevisionNumber,
    actualRevisionNumber: error.actualRevisionNumber,
    latestTitle: error.latestTitle,
    latestContent: error.latestContent,
  }
}

export function asReadOnlyReplica(error: MutationErrorFragment | null | undefined): ReadOnlyReplica | null {
  if (error?.kind !== 'ReadOnlyReplica') return null
  return { spaceId: error.spaceId, originInstanceId: error.originInstanceId }
}

/** design.md §6.4.1/§6.7: a refused subtree operation reports only a count, never identities. */
export function blockedPageCount(error: MutationErrorFragment | null | undefined): number | null {
  if (error?.kind !== 'SubtreeOperationForbidden') return null
  return error.blockedPageCount
}

/**
 * Fallback copy for the kinds that don't get a dedicated flow. Returns null
 * for no error, and for the kinds a caller should have already handled via
 * the narrowing helpers above (so a forgotten StaleRevision handler shows
 * *something* — but the merge/replica flows are the designed UX).
 */
export function describeMutationError(error: MutationErrorFragment | null | undefined): string | null {
  if (!error) return null
  switch (error.kind) {
    case 'Forbidden':
      return error.message ? `Not permitted: ${error.message}` : 'Not permitted.'
    case 'NotFound':
      // Same "absent, not forbidden" voice as everywhere else (design.md §6.7).
      return 'Not found.'
    case 'Validation':
      return error.message ?? 'Invalid input.'
    case 'SubtreeOperationForbidden': {
      const count = error.blockedPageCount ?? 0
      return `${count} page${count === 1 ? '' : 's'} in this subtree couldn't be included (you don't have permission).`
    }
    case 'StaleRevision':
      return 'Someone else saved changes first.'
    case 'ReadOnlyReplica':
      return 'This space is a read-only replica.'
    default:
      return error.message ?? 'The request was refused.'
  }
}
