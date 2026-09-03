import type { ClassificationLevel } from '../graphql/generated/graphql'

/**
 * design.md §21's gates, client-side — the one place the SPA compares a
 * marking against a principal.
 *
 * This exists so the marking control can PREVENT rather than refuse: §21.6
 * refuses a marking above the caller's clearance, and refuses a resulting
 * marking the caller could not then read — by level, by selector (§21.15) or
 * by caveat — all as ForbiddenError. Offering those and then reporting the
 * refusal is the failure mode this module is here to avoid. It is an
 * affordance, never an authority — `MarkingGate` on the server decides, and
 * this file's only job is to agree with it.
 */

/**
 * §21.1: the scheme is "fixed, ordered, and not configurable", which is
 * exactly what makes writing it down safe — there is no fifth level to
 * insert, and the server hard-codes the same ladder as a load-bearing
 * numeric enum for the same reason.
 *
 * The generated `ClassificationLevel` is a TypeScript string union and
 * carries no runtime order to read, so the order is stated once, here, and
 * pinned to the union in both directions: `satisfies` rejects a member the
 * schema does not have, and the `AssertNever` below rejects a member the
 * schema gained that nobody placed in the ladder. A level inserted in the
 * middle would silently re-rank everything below it (§21.1), so that has to
 * break the build rather than compile.
 *
 * **This is the COMPARISON, not the presentation.** `Query.classificationScheme`
 * supplies the picker's options, their display spellings and the order they
 * render in, and the control uses it for all three — the SPA owns no spelling
 * and no display order. What the scheme deliberately does not carry is a
 * rank, so the one thing that cannot come from it is this: whether a level is
 * at or above `me.clearance`. That comparison has to be synchronous (a
 * disabled radio cannot wait on a query), has to survive the query failing,
 * and has to fail closed when it does — none of which a fetched ordering
 * gives. Hence a hard-coded ladder that cannot silently disagree with the
 * schema, rather than a derived one that could silently be absent.
 */
export const CLASSIFICATION_LADDER = [
  'OFFICIAL',
  'OFFICIAL_SENSITIVE',
  'SECRET',
  'TOP_SECRET',
] as const satisfies readonly ClassificationLevel[]

type AssertNever<T extends never> = T
/** Compile-time only: fails to typecheck if codegen adds a level the ladder does not place. */
export type EveryLevelIsPlaced = AssertNever<Exclude<ClassificationLevel, (typeof CLASSIFICATION_LADDER)[number]>>

/**
 * §21.3's floor: what an absent, unrecognised or malformed clearance is
 * worth. OFFICIAL-SENSITIVE, not OFFICIAL — the two are the everyday tiers
 * and an unconfigured mapper should leave both readable; the outage argument
 * is unchanged, the line sits one notch higher. `me.clearance` already
 * arrives resolved to this on the wire; this constant is for the one path
 * that cannot read the wire, a value the ladder cannot place.
 */
export const DEFAULT_CLEARANCE: ClassificationLevel = 'OFFICIAL_SENSITIVE'

/**
 * Where a PAGE's level sits on the ladder. An unrecognised value ranks ABOVE
 * the top: §21.3 normalizes an out-of-ladder stored level to TOP SECRET
 * rather than trusting it, because the two failure directions are not
 * symmetric — a value above the ladder denies everyone (noisy but harmless),
 * while one that compares as low would make the page look readable by
 * everybody.
 *
 * Same direction as the server, deliberately one notch stricter, and the
 * difference is worth knowing before anyone "corrects" it: `ProtectiveMarking`
 * normalizes an unknown level *to* TOP SECRET, so the server would still admit
 * a TOP_SECRET principal to it; ranking above the top denies even them. The
 * only way the two can disagree is a server ahead of this client, and on that
 * path an affordance should be the stricter side — greying out a level the
 * server might have allowed costs a refused click, while offering one it
 * refuses is the failure this module exists to prevent.
 *
 * **design.md §21.3 records this divergence from the other side** and asks that
 * the two be revisited in the same change. That paragraph cites this file; this
 * is the pointer back, so whoever starts here finds the shared record rather
 * than re-deriving which direction is correct.
 */
export function markingLevelRank(level: ClassificationLevel): number {
  const index = CLASSIFICATION_LADDER.indexOf(level)
  return index === -1 ? CLASSIFICATION_LADDER.length : index
}

/**
 * Where a PRINCIPAL's clearance sits — and the fail-closed direction is the
 * opposite one. §21.3: an absent, unrecognised or malformed clearance is
 * worth OFFICIAL-SENSITIVE and nothing more, so an unplaceable clearance
 * ranks at the floor rather than the top.
 */
export function clearanceRank(clearance: ClassificationLevel): number {
  const index = CLASSIFICATION_LADDER.indexOf(clearance)
  return index === -1 ? CLASSIFICATION_LADDER.indexOf(DEFAULT_CLEARANCE) : index
}

/** §21.2: a principal may view a page when their clearance is at or above the page's level. */
export function levelIsWithinClearance(level: ClassificationLevel, clearance: ClassificationLevel): boolean {
  return markingLevelRank(level) <= clearanceRank(clearance)
}

/**
 * §21.4's canonical form: trimmed and upper-cased. That upper-casing is the
 * section's documented, deliberate departure from §6.3's ordinal matching,
 * confined to this one comparison — the marking's vocabulary and the OIDC
 * claim mapper were never guaranteed to agree on case, and failing closed on
 * a casing difference is an outage, not security. `toUpperCase` (not
 * `toLocaleUpperCase`) so the result cannot depend on the browser's locale.
 *
 * Applied to BOTH sides here even though §21.6 has the server canonicalize
 * `me.nationality` before sending it. That is not redundancy worth removing:
 * the wire representation is not this module's to assume, the comparison has
 * to be right on its own terms, and being wrong about it is silent — it warns
 * an author out of a marking that would have worked rather than failing
 * loudly. One canonicalizer, both sides, is the same rule §21.4 applies to the
 * marking itself.
 */
export function canonicalCountry(value: string): string {
  return value.trim().toUpperCase()
}

/** Canonical, de-duplicated, ordinally sorted — the form §21.4 stores and compares. */
export function canonicalCountries(values: readonly string[]): string[] {
  const seen = new Set<string>()
  for (const value of values) {
    const canonical = canonicalCountry(value)
    if (canonical.length > 0) seen.add(canonical)
  }
  return [...seen].sort()
}

/**
 * §21.4: a principal must hold at least one nationality value in the set. An
 * empty set means no caveat and admits everyone; an absent or empty
 * nationality therefore denies any page carrying one — fail closed.
 */
export function eyesOnlyAdmits(eyesOnly: readonly string[], nationality: readonly string[]): boolean {
  const required = canonicalCountries(eyesOnly)
  if (required.length === 0) return true
  const held = new Set(canonicalCountries(nationality))
  return required.some((country) => held.has(country))
}

/** One selector on a marking or a grant (§21.15): a category name and one of its values. Both canonical UPPER on the wire. */
export interface SelectorValue {
  category: string
  value: string
}

/**
 * The same canonical form for a selector token as for a country. The catalog
 * stores names and values UPPER and the server compares them ordinally; this
 * folds both sides here for the same reason `canonicalCountry` does — the
 * comparison has to be right on its own terms, and a false refusal in the
 * affordance is silent.
 */
function canonicalToken(value: string): string {
  return value.trim().toUpperCase()
}

/**
 * §21.15's ELIGIBILITY gate, the site-wide half: the principal's token
 * satisfies the category's attribute (or the category has none).
 * `me.selectorEligibility` lists the category NAMES this caller is eligible
 * for, resolved server-side; this only asks whether one is among them.
 */
export function selectorEligible(category: string, eligibility: readonly string[]): boolean {
  const wanted = canonicalToken(category)
  return eligibility.some((name) => canonicalToken(name) === wanted)
}

/**
 * §21.15's GRANT gate, the per-space half: the value is among those the
 * caller's matching access grants confer here (the union across them, §6.4).
 */
export function selectorGranted(selector: SelectorValue, grants: readonly SelectorValue[]): boolean {
  const category = canonicalToken(selector.category)
  const value = canonicalToken(selector.value)
  return grants.some((grant) => canonicalToken(grant.category) === category && canonicalToken(grant.value) === value)
}

/** The caller's own clearance, nationality, eligibility and per-space grants, as `me` and the space report them (§21). */
export interface ViewerClearance {
  clearance: ClassificationLevel
  nationality: readonly string[]
  /** Category names, from `me.selectorEligibility`. */
  selectorEligibility: readonly string[]
  /**
   * The values granted to the caller in THIS space (`Space.viewerSelectorGrants`),
   * or null while that read has not answered. Null claims no refusal, for the
   * same reason a missing `me` does below: a grant refusal the SPA cannot
   * justify would hide a value from someone entitled to it, and the server
   * still decides.
   */
  selectorGrants: readonly SelectorValue[] | null
}

/**
 * The level, selector set and country set `setPageMarking` would replace the
 * marking with. The UK prefix is absent on purpose — see `markingRefusal`.
 */
export interface DraftMarking {
  level: ClassificationLevel
  eyesOnly: readonly string[]
  selectors: readonly SelectorValue[]
}

export type MarkingRefusal =
  /** §21.6, level half: the caller's clearance is below the level they picked. */
  | { kind: 'ABOVE_CLEARANCE'; level: ClassificationLevel }
  /** §21.15, eligibility half: the caller's token does not qualify them for this category at all. */
  | { kind: 'SELECTOR_NOT_ELIGIBLE'; category: string }
  /** §21.15, grant half: eligible, but no access grant in this space confers this value on the caller. */
  | { kind: 'SELECTOR_NOT_GRANTED'; category: string; value: string }
  /** §21.6, caveat half: the resulting eyes-only set holds none of the caller's own nationalities. */
  | { kind: 'EYES_ONLY_EXCLUDES_YOU'; viewerHasNoNationality: boolean }

/**
 * Whether §21.6's "you may not set a marking you could not then read" would
 * refuse this draft, and which gate would refuse it.
 *
 * The rule is evaluated on the resulting marking AS A WHOLE, which is what
 * mechanizes its stated reason: marking a page `SECRET US EYES ONLY` as a
 * UK-national editor, or `OFFICIAL BANANA` as an editor granted only APPLE,
 * loses you the page just as completely as over-classifying it does.
 *
 * The UK prefix is deliberately not an input. §21.12 makes it presentational
 * with no access-control considerations whatsoever — the server's gate never
 * reads it, so there is no prefix a caller can be refused for. Giving it
 * semantics here "for completeness" would invent a constraint the server
 * does not have.
 *
 * Order matches the server's ladder (classification, then per selector its
 * eligibility and then its grant, then the caveat): the first failing gate is
 * reported, because it is the one the server would name.
 */
export function markingRefusal(draft: DraftMarking, viewer: ViewerClearance | null): MarkingRefusal | null {
  // No `me` yet (the query is still in flight, or it failed) means the SPA
  // knows nothing about this caller. Claiming a refusal it cannot justify
  // would hide levels from someone entitled to them; the server still
  // decides, and the caller sees the real refusal if there is one.
  if (!viewer) return null

  if (!levelIsWithinClearance(draft.level, viewer.clearance)) {
    return { kind: 'ABOVE_CLEARANCE', level: draft.level }
  }
  for (const selector of draft.selectors) {
    if (!selectorEligible(selector.category, viewer.selectorEligibility)) {
      return { kind: 'SELECTOR_NOT_ELIGIBLE', category: selector.category }
    }
    if (viewer.selectorGrants !== null && !selectorGranted(selector, viewer.selectorGrants)) {
      return { kind: 'SELECTOR_NOT_GRANTED', category: selector.category, value: selector.value }
    }
  }
  if (!eyesOnlyAdmits(draft.eyesOnly, viewer.nationality)) {
    return { kind: 'EYES_ONLY_EXCLUDES_YOU', viewerHasNoNationality: canonicalCountries(viewer.nationality).length === 0 }
  }
  return null
}

/**
 * The short reason a level is not offered. Sits beside each unavailable
 * option rather than appearing after a refusal — the whole point of §21.6
 * being mirrored here is that the user reads why before choosing, not after.
 */
export const ABOVE_CLEARANCE_REASON = 'Above your clearance'

/** The short reason a whole category picker is unavailable (§21.15's eligibility gate). */
export function notEligibleReason(category: string): string {
  return `Not eligible for ${category} material`
}

/** The short reason one value is not offered (§21.15's grant gate). */
export const NOT_GRANTED_REASON = 'Not granted to you in this space'

/** One sentence per refusal, stating the constraint rather than apologising for it. */
export function describeMarkingRefusal(refusal: MarkingRefusal): string {
  switch (refusal.kind) {
    case 'ABOVE_CLEARANCE':
      return `${ABOVE_CLEARANCE_REASON} — you cannot set a marking you would not be cleared to read.`
    case 'SELECTOR_NOT_ELIGIBLE':
      return `Not eligible for ${refusal.category} material, so you could not read this page after marking it.`
    case 'SELECTOR_NOT_GRANTED':
      return `${refusal.value} is not granted to you in this space, so you could not read this page after marking it.`
    case 'EYES_ONLY_EXCLUDES_YOU':
      return refusal.viewerHasNoNationality
        ? 'You hold no nationality value, so any eyes-only set would leave you unable to read this page.'
        : 'This eyes-only set holds none of your own nationalities, so you would no longer be able to read this page.'
  }
}
