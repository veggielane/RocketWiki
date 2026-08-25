/**
 * design.md §21.4: the eyes-only caveat's country vocabulary is the
 * registered `nationality` attribute's `allowedValues` (§6.2) — the SAME
 * attribute the rule engine's `attr` conditions already match on, and the
 * same one `ClearanceGate` enforces the caveat against. There is no ISO 3166
 * list anywhere in this feature, deliberately: a hard-coded one would let an
 * admin pick `GB` on a wiki whose tokens say `UK`, and the resulting mismatch
 * fails *closed* — the page quietly becomes invisible to everybody, including
 * the audience it names, while the marking reads as perfectly correct.
 *
 * The key is a constant rather than a literal at the call site because it is
 * a well-known key shared with the server (`ClearanceGate.NationalityAttributeKey`),
 * not a string this screen chose.
 */
export const NATIONALITY_ATTRIBUTE_KEY = 'nationality'
