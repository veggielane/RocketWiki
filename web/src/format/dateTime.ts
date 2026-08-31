/**
 * The API hands out instants as UTC ISO-8601 strings (design.md §8 — every
 * `*AtUtc` field). Four surfaces independently wrote
 * `new Date(x).toLocaleString()`, which is the right default — the viewer's
 * own locale and timezone, chosen by the browser rather than guessed by us —
 * but four copies is four places to disagree the next time anyone tunes it.
 *
 * Deliberately no options object: passing explicit `dateStyle`/`timeStyle`
 * would override the conventions the user has actually configured, and none
 * of the callers need a format narrower than "when did this happen".
 *
 * NOT for the audit log: that grid shows raw UTC on purpose (design.md §14 —
 * an export-control audit trail is read across timezones and must not shift
 * under the reader).
 *
 * Relative phrasing lives next door in relativeTime.ts, which answers a
 * different question: this one says WHEN something happened, that one says how
 * long ago. The homepage feeds want the second — a stale list is sorted by it,
 * so the phrasing is what makes the order legible — the audit log must have
 * the first, and the 30-day trash window keeps its own domain voice in
 * trash/trashCountdown.ts.
 */
export function formatTimestamp(utcIso: string): string {
  return new Date(utcIso).toLocaleString()
}
