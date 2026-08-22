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
    return 'This space is a read-only replica (design.md §12) — editing is unconditionally disabled here.'
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
