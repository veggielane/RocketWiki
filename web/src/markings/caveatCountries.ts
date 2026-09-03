import type { NationalCaveatCountry } from '../graphql/generated/graphql'

/**
 * design.md §21.4: the eyes-only caveat's vocabulary is the FIXED set of five
 * — AUS, CAN, NZ, UK, US — not a registered attribute's values and not an ISO
 * list. This is the picker's option list, pinned to the schema's input enum
 * in both directions exactly as `CLASSIFICATION_LADDER` pins the levels:
 * `satisfies` rejects a member the enum does not have, and the `AssertNever`
 * below rejects a member the enum gained that nobody listed here. A sixth
 * country therefore breaks the build rather than compiling into a picker
 * that silently offers less than the server accepts — or, the other way
 * round, one that offers a token the server would reject with a 400.
 *
 * Why a fixed set is now safe where a hard-coded list once was not: the same
 * five tokens are pinned in the API's enum, in this file, and in the Keycloak
 * README, so a token mismatch (`GB` in a token where the marking says `UK`)
 * is a deployment defect caught at first login rather than a representable
 * state the wiki has to fail closed on forever.
 *
 * The order here is the order the picker offers them — alphabetical, which is
 * also how the server's formatter lists them in a label (`AUS/NZ EYES ONLY`).
 */
export const NATIONAL_CAVEAT_COUNTRIES = ['AUS', 'CAN', 'NZ', 'UK', 'US'] as const satisfies readonly NationalCaveatCountry[]

type AssertNever<T extends never> = T
/** Compile-time only: fails to typecheck if codegen adds a country this list does not pin. */
export type EveryCountryIsPinned = AssertNever<Exclude<NationalCaveatCountry, (typeof NATIONAL_CAVEAT_COUNTRIES)[number]>>

/**
 * Whether a stored token is one the mutation can send. `PageMarkingView.eyesOnly`
 * is a list of strings on OUTPUT precisely so a legacy token stored before the
 * vocabulary was fixed can still be read; this is the gate between reading
 * such a marking and offering to write it back.
 */
export function isNationalCaveatCountry(value: string): value is NationalCaveatCountry {
  return (NATIONAL_CAVEAT_COUNTRIES as readonly string[]).includes(value)
}
