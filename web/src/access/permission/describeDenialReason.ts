/**
 * Human-readable text for the denial-reason codes
 * `EffectivePermissionCalculator.Compute` produces
 * (`RocketWiki.Core/Access/EffectivePermissionCalculator.cs`): "no-space-role",
 * "replica-read-only", "insufficient-space-role", or the dynamic
 * `restriction:{pageId}:{ruleId}` form. Kept as a pure function, separate
 * from the display component, so the mapping is unit-testable without
 * mounting anything.
 */
export function describeDenialReason(reason: string | null): string | null {
  if (reason === null) {
    return null
  }
  if (reason === 'no-space-role') {
    return 'No space grant matches this user — they have no role in this space at all.'
  }
  if (reason === 'replica-read-only') {
    // The "(design.md §12)" this used to carry pointed at a document nobody
    // reading a denial can open. What it was standing in for — that the
    // read-only-ness comes from the sync direction, and so no grant can widen
    // it — is said in words instead.
    return 'This space is a read-only replica: its content arrives by one-way sync from another instance, so editing is disabled here whatever grants the user holds.'
  }
  if (reason === 'insufficient-space-role') {
    return "The user's space role is viewer, which is below editor."
  }
  const restrictionMatch = /^restriction:(.+):(.+)$/.exec(reason)
  if (restrictionMatch) {
    return `Blocked by a restriction rule on page ${restrictionMatch[1]}.`
  }
  return reason
}
