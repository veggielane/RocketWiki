/**
 * design.md §6.4.1/§6.7: a refused subtree delete reports only a COUNT of
 * blocked descendants, never their identities — naming them would confirm
 * their existence to someone who may have no right to know. This function
 * is the single place that turns that count into copy, so nothing
 * upstream is tempted to also pass along page titles/ids "just this once."
 */
export function describeDeleteOutcome(blockedDescendantCount: number | null | undefined): string | null {
  if (!blockedDescendantCount || blockedDescendantCount <= 0) {
    return null
  }
  const noun = blockedDescendantCount === 1 ? 'page' : 'pages'
  return `${blockedDescendantCount} ${noun} in this subtree couldn't be deleted (you don't have permission).`
}
