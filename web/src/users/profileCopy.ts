/**
 * Every sentence the profile page puts in front of a reader, in one place so
 * the design-doc-reference guard (feedback/__tests__/noDesignDocRefsInCopy)
 * can walk it, and so the page's two load-bearing claims cannot drift:
 *
 * - what it shows was recorded at the person's LAST SIGN-IN (no timestamp —
 *   the page says "last sign-in" in words and nothing more precise), and
 * - none of it is edited here. Clearance and eligibility are Keycloak's;
 *   RocketWiki mirrors what the token carried and shows the gate's reading.
 */

/** The footer every recorded profile carries. */
export const PROFILE_RECORDED_CAPTION =
  "Recorded at this user's last sign-in. Clearance and eligibility are managed in Keycloak, not in RocketWiki."

/**
 * `clearanceRecorded: false`. The wire still carries a level — the
 * OFFICIAL-SENSITIVE floor the gate falls back to — and this is the sentence
 * shown INSTEAD of it. The floor is what the gate does with an absent claim,
 * not a clearance this person holds, and a page that badged it would tell a
 * colleague they may be shown material the person was never cleared for.
 */
export const CLEARANCE_NOT_RECORDED = 'Not recorded'
export const CLEARANCE_NOT_RECORDED_DETAIL = "No recognised clearance claim was recorded at this user's last sign-in."

/**
 * `isExternal`: a shadow account created by sync that has never signed in
 * here. Nothing below the name was ever recorded, so the page says why there
 * are no sections rather than rendering two empty ones.
 */
export const EXTERNAL_ACCOUNT_NOTE =
  'This account was created by sync and has never signed in here, so no clearance or selector eligibility has been recorded for it. Both are recorded at sign-in and managed in Keycloak, not in RocketWiki.'

/** Under the Selectors heading: what the table answers, and what it deliberately does not. */
export const SELECTOR_SECTION_DESCRIPTION =
  "Whether this user may be shown material in each selector category. Eligibility is site-wide; the values they hold in a particular space come from that space's grants and are not shown here."

/** An instance with no selector catalog: the fact and the consequence (web/README.md's empty-state rule). */
export const NO_SELECTOR_CATEGORIES =
  'No selector categories are configured on this instance, so there is nothing to be eligible for.'

export interface EligibilityStatus {
  /** False when the category has no claim gate, in which case `eligible` is true for everyone. */
  requiresAttribute: boolean
  eligible: boolean
}

export type EligibilityKind = 'EVERYONE' | 'ELIGIBLE' | 'NOT_ELIGIBLE'

/**
 * Which of the three things a row can say. Split from the words so the page
 * can pick an icon and a tone from the same decision the text came from —
 * the icon and colour are accents beside the words, never a second reading.
 */
export function eligibilityKind(status: EligibilityStatus): EligibilityKind {
  if (!status.requiresAttribute) return 'EVERYONE'
  return status.eligible ? 'ELIGIBLE' : 'NOT_ELIGIBLE'
}

/**
 * The row's text. "Everyone is eligible" rather than "Eligible" for an
 * ungated category: the two are different facts — one is about this person's
 * claims, the other about the category — and a colleague deciding whether to
 * share something needs to know which they are looking at.
 */
export function describeEligibility(status: EligibilityStatus): string {
  switch (eligibilityKind(status)) {
    case 'EVERYONE':
      return 'Everyone is eligible'
    case 'ELIGIBLE':
      return 'Eligible'
    case 'NOT_ELIGIBLE':
      return 'Not eligible'
  }
}
