/**
 * The two ways a marking can shut its own author out, mirrored client-side —
 * the one place the SPA compares a marking against a person.
 *
 * This exists so the marking control can PREVENT rather than refuse. The
 * server refuses a marking its author could not then read — a selector value
 * no access grant confers on them in this space (§21.15), or an eyes-only
 * caveat holding none of their own nationalities (§21.4) — as ForbiddenError,
 * and offering those and then reporting the refusal is the failure mode this
 * module is here to avoid. It is an affordance, never an authority —
 * `MarkingGate` on the server decides, and this file's only job is to agree
 * with it.
 *
 * What is deliberately NOT here: any comparison of the classification level
 * against the caller. This deployment carries no clearance attribute, so the
 * level is display — compared against nobody, exactly like the UK prefix —
 * and any editor may set any level. A level ladder and a clearance comparison
 * used to live in this file (it was `clearance.ts`); they were removed when
 * the server stopped making the check, because an affordance with no gate to
 * agree with can only ever be wrong.
 */

/**
 * §21.4's canonical form: trimmed and upper-cased. That upper-casing is the
 * section's documented, deliberate departure from §6.3's ordinal matching,
 * confined to this one comparison — the marking's vocabulary and the OIDC
 * claim mapper were never guaranteed to agree on case, and failing closed on
 * a casing difference is an outage, not security. `toUpperCase` (not
 * `toLocaleUpperCase`) so the result cannot depend on the browser's locale.
 *
 * Applied to BOTH sides here even though the server canonicalizes
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
 * §21.15's grant gate — the whole of the selector gate, now that a category
 * carries no attribute of its own: the value is among those the caller's
 * matching access grants confer in this space (the union across them, §6.4).
 */
export function selectorGranted(selector: SelectorValue, grants: readonly SelectorValue[]): boolean {
  const category = canonicalToken(selector.category)
  const value = canonicalToken(selector.value)
  return grants.some((grant) => canonicalToken(grant.category) === category && canonicalToken(grant.value) === value)
}

/**
 * What the SPA knows about the caller that a marking could exclude: their own
 * nationality (from `me`) and the selector values granted to them in this
 * space (from the space). Nothing else about a person bears on whether they
 * may read a marking.
 */
export interface MarkingViewer {
  nationality: readonly string[]
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
 * The selector set and country set `setPageMarking` would replace the
 * marking with. The level and the UK prefix are absent on purpose — see
 * `markingRefusal`.
 */
export interface DraftMarking {
  eyesOnly: readonly string[]
  selectors: readonly SelectorValue[]
}

export type MarkingRefusal =
  /** §21.15: no access grant in this space confers this value on the caller. */
  | { kind: 'SELECTOR_NOT_GRANTED'; category: string; value: string }
  /** §21.4: the resulting eyes-only set holds none of the caller's own nationalities. */
  | { kind: 'EYES_ONLY_EXCLUDES_YOU'; viewerHasNoNationality: boolean }

/**
 * Whether "you may not set a marking you could not then read" would refuse
 * this draft, and which gate would refuse it.
 *
 * The rule is evaluated on the resulting marking AS A WHOLE, which is what
 * mechanizes its stated reason: marking a page `SECRET US EYES ONLY` as a
 * UK-national editor, or `OFFICIAL BANANA` as an editor granted only APPLE,
 * loses you the page.
 *
 * Neither the level nor the UK prefix is an input, and both absences are
 * load-bearing. The prefix is presentational with no access-control
 * considerations whatsoever (§21.12), and in this deployment the level is the
 * same — there is no clearance for it to be measured against — so the
 * server's gate reads neither, and there is no level or prefix a caller can
 * be refused for. Giving either semantics here "for completeness" would
 * invent a constraint the server does not have.
 *
 * Order matches the server's ladder (per selector its grant, then the
 * caveat): the first failing gate is reported, because it is the one the
 * server would name.
 */
export function markingRefusal(draft: DraftMarking, viewer: MarkingViewer | null): MarkingRefusal | null {
  // No `me` yet (the query is still in flight, or it failed) means the SPA
  // knows nothing about this caller. Claiming a refusal it cannot justify
  // would hide values from someone entitled to them; the server still
  // decides, and the caller sees the real refusal if there is one.
  if (!viewer) return null

  for (const selector of draft.selectors) {
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
 * The short reason one value is not offered (§21.15's grant gate). Sits
 * beside the unavailable option rather than appearing after a refusal — the
 * whole point of the rule being mirrored here is that the user reads why
 * before choosing, not after.
 */
export const NOT_GRANTED_REASON = 'Not granted to you in this space'

/** One sentence per refusal, stating the constraint rather than apologising for it. */
export function describeMarkingRefusal(refusal: MarkingRefusal): string {
  switch (refusal.kind) {
    case 'SELECTOR_NOT_GRANTED':
      return `${refusal.value} is not granted to you in this space, so you could not read this page after marking it.`
    case 'EYES_ONLY_EXCLUDES_YOU':
      return refusal.viewerHasNoNationality
        ? 'You hold no nationality value, so any eyes-only set would leave you unable to read this page.'
        : 'This eyes-only set holds none of your own nationalities, so you would no longer be able to read this page.'
  }
}
