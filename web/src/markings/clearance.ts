import type { ClassificationLevel } from '../graphql/generated/graphql'

/**
 * design.md §21's ordering rule, client-side — the one place the SPA compares
 * a classification against anything.
 *
 * This exists so the marking control can PREVENT rather than refuse: §21.6
 * refuses a marking above the caller's clearance, and refuses a resulting
 * marking the caller could not then read, both as ForbiddenError. Offering
 * those and then reporting the refusal is the failure mode this module is
 * here to avoid. It is an affordance, never an authority — `ClearanceGate`
 * on the server decides, and this file's only job is to agree with it.
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
 * Where a PAGE's level sits on the ladder. An unrecognised value ranks ABOVE
 * the top: §21.3 normalizes an out-of-ladder stored level to TOP SECRET
 * rather than trusting it, because the two failure directions are not
 * symmetric — a value above the ladder denies everyone (noisy but harmless),
 * while one that compares as low would make the page look readable by
 * everybody. The SPA takes the same direction for the same reason.
 */
export function markingLevelRank(level: ClassificationLevel): number {
  const index = CLASSIFICATION_LADDER.indexOf(level)
  return index === -1 ? CLASSIFICATION_LADDER.length : index
}

/**
 * Where a PRINCIPAL's clearance sits — and the fail-closed direction is the
 * opposite one. §21.3: "absent, unrecognised, or malformed clearance grants
 * OFFICIAL — and only OFFICIAL", so an unplaceable clearance ranks at the
 * bottom of the ladder rather than the top.
 */
export function clearanceRank(clearance: ClassificationLevel): number {
  const index = CLASSIFICATION_LADDER.indexOf(clearance)
  return index === -1 ? 0 : index
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
 * §21.12: the prefix is normalized on write exactly like the country values —
 * trimmed and upper-cased — and null, `""` and whitespace all collapse to the
 * same "no prefix" state, which is legal and must stay reachable.
 */
export function canonicalPrefix(value: string | null | undefined): string | null {
  const canonical = (value ?? '').trim().toUpperCase()
  return canonical.length === 0 ? null : canonical
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

/** The caller's own clearance and nationality, as `me` reports them (§21). */
export interface ViewerClearance {
  clearance: ClassificationLevel
  nationality: readonly string[]
}

/** The level + country set `setPageMarking` would replace the marking with. The prefix is absent on purpose — see `markingRefusal`. */
export interface DraftMarking {
  level: ClassificationLevel
  eyesOnly: readonly string[]
}

export type MarkingRefusal =
  /** §21.6, level half: the caller's clearance is below the level they picked. */
  | { kind: 'ABOVE_CLEARANCE'; level: ClassificationLevel }
  /** §21.6, caveat half: the resulting eyes-only set holds none of the caller's own nationalities. */
  | { kind: 'EYES_ONLY_EXCLUDES_YOU'; viewerHasNoNationality: boolean }

/**
 * Whether §21.6's "you may not set a marking you could not then read" would
 * refuse this draft, and which half of it.
 *
 * The rule is evaluated on the resulting marking AS A WHOLE, which is what
 * mechanizes its stated reason: marking a page `SECRET [US EYES ONLY]` as a
 * GB-national editor loses you the page just as completely as
 * over-classifying it does.
 *
 * The prefix is deliberately not an input. §21.12 makes it presentational
 * with no access-control considerations whatsoever — the server's rule is
 * `ClearanceGate.Check(resultingMarking, principal)` and the gate never reads
 * the prefix, so there is no prefix a caller can be refused for. Giving it
 * semantics here "for completeness" would invent a constraint the server
 * does not have.
 *
 * Order matches the gate: the level is reported first because it is the
 * coarser fact (§21.4).
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

/** One sentence per refusal, stating the constraint rather than apologising for it. */
export function describeMarkingRefusal(refusal: MarkingRefusal): string {
  switch (refusal.kind) {
    case 'ABOVE_CLEARANCE':
      return `${ABOVE_CLEARANCE_REASON} — you cannot set a marking you would not be cleared to read.`
    case 'EYES_ONLY_EXCLUDES_YOU':
      return refusal.viewerHasNoNationality
        ? 'You hold no nationality value, so any eyes-only set would leave you unable to read this page.'
        : 'This eyes-only set holds none of your own nationalities, so you would no longer be able to read this page.'
  }
}
