/**
 * Every sentence the profile page puts in front of a reader, in one place so
 * the design-doc-reference guard (feedback/__tests__/noDesignDocRefsInCopy)
 * can walk it, and so the page's two load-bearing claims cannot drift:
 *
 * - what it shows was recorded at the person's LAST SIGN-IN (no timestamp —
 *   the page says "last sign-in" in words and nothing more precise), and
 * - none of it is edited here. Group membership is Keycloak's; RocketWiki
 *   mirrors what the token carried.
 *
 * There is no clearance sentence and no eligibility sentence, because the
 * page shows neither: this deployment carries no clearance attribute and no
 * per-category selector attribute, so a level is compared against nobody and
 * a selector is gated by the grants in each space. The profile says what a
 * person is a member of, never what they may read.
 */

/** The footer every recorded profile carries. */
export const PROFILE_RECORDED_CAPTION =
  "Recorded at this user's last sign-in. Group membership is managed in Keycloak, not in RocketWiki."

/** Under the Groups heading: what the list is, and what it deliberately does not answer. */
export const GROUPS_SECTION_DESCRIPTION =
  "The groups this user's sign-in carried, which access rules and space grants are written against. Whether they may read a particular page is decided per space and per page, and is not shown here."

/**
 * A recorded profile whose token carried no groups claim, or an empty one.
 * The fact and when it was true, so a reader does not take an empty list for
 * "this person is in no groups" in general — only that none arrived last
 * time.
 */
export const NO_GROUPS_RECORDED = "No group memberships were recorded at this user's last sign-in."

/**
 * `isExternal`: a shadow account created by sync that has never signed in
 * here. Nothing below the name was ever recorded, so the page says why there
 * is no section rather than rendering an empty one.
 */
export const EXTERNAL_ACCOUNT_NOTE =
  'This account was created by sync and has never signed in here, so no group membership has been recorded for it. Groups are recorded at sign-in and managed in Keycloak, not in RocketWiki.'
