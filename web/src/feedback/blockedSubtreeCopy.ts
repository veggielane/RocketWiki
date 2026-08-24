/**
 * design.md §6.4.1/§6.7: a subtree operation the caller may not perform in
 * full reports a COUNT of blocked pages and nothing else — naming them would
 * confirm their existence to someone who may have no right to know.
 *
 * Two callers say this: the typed `SubtreeOperationForbidden` payload
 * (graphql/mutationError.ts), which covers any subtree operation, and the
 * delete flow (trash/describeDeleteOutcome.ts), which knows the operation
 * was a delete and says so. They drifted to one word apart; one word apart
 * is exactly the drift that becomes two voices after the next edit, so the
 * sentence lives here and the verb is the parameter.
 */
export function describeBlockedSubtree(
  blockedCount: number | null | undefined,
  /** Past participle of what could not happen to those pages. */
  outcome: 'included' | 'deleted',
): string | null {
  if (!blockedCount || blockedCount <= 0) {
    return null
  }
  const noun = blockedCount === 1 ? 'page' : 'pages'
  return `${blockedCount} ${noun} in this subtree couldn't be ${outcome} (you don't have permission).`
}
