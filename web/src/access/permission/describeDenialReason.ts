import { MARKING_UNAVAILABLE } from '../denial/protectedCopy'

/**
 * Human-readable text for the denial-reason tokens
 * `EffectivePermissionCalculator` produces
 * (`RocketWiki.Core/Access/EffectivePermissionCalculator.cs`) — the audit's
 * first-failing-gate token, one per gate on the ladder: `no-space-access`,
 * `marking:unavailable`, `selector:unknown:{CATEGORY}`,
 * `selector:not_granted:{CATEGORY}`, `caveat:eyes_only`,
 * `restriction:{pageId}:{ruleId}`, then the edit-only `replica-read-only`
 * and `insufficient-space-role`. Kept as a pure function, separate from the
 * display component, so the mapping is unit-testable without mounting
 * anything.
 *
 * A token this build does not know falls back to the raw string: a server
 * ahead of this client still gets its reason shown rather than swallowed.
 * That is also where `classification:{level}` and
 * `selector:not_eligible:{CATEGORY}` land now: neither is minted any more —
 * this deployment compares no level against a person and gates a selector by
 * grant alone — so an old audit row carrying one shows its raw token, which
 * is the honest reading of a reason the ladder no longer has.
 */
export function describeDenialReason(reason: string | null): string | null {
  if (reason === null) {
    return null
  }
  if (reason === 'no-space-access') {
    return 'No access grant in this space matches this user.'
  }
  if (reason === 'marking:unavailable') {
    return MARKING_UNAVAILABLE
  }
  if (reason === 'replica-read-only') {
    // The "(design.md §12)" this used to carry pointed at a document nobody
    // reading a denial can open. What it was standing in for — that the
    // read-only-ness comes from the sync direction, and so no grant can widen
    // it — is said in words instead.
    return 'This space is a read-only replica: its content arrives by one-way sync from another instance, so editing is disabled here whatever grants the user holds.'
  }
  if (reason === 'insufficient-space-role') {
    return 'No role grant gives this user Editor or Space admin in this space.'
  }
  if (reason === 'caveat:eyes_only') {
    return "This page's national caveat admits none of this user's nationalities."
  }
  const unknownSelector = /^selector:unknown:(.+)$/.exec(reason)
  if (unknownSelector) {
    return `This page carries a ${unknownSelector[1]} selector this instance does not configure, so nobody can read it here.`
  }
  const notGranted = /^selector:not_granted:(.+)$/.exec(reason)
  if (notGranted) {
    return `No access grant in this space gives this user the page's ${notGranted[1]} value.`
  }
  const restrictionMatch = /^restriction:(.+):(.+)$/.exec(reason)
  if (restrictionMatch) {
    return `Blocked by a restriction rule on page ${restrictionMatch[1]}.`
  }
  return reason
}
