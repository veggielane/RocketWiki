/**
 * What a saved property row actually means at the API (design.md §20).
 *
 * The backend deliberately keeps one mutation to one meaning:
 * `setPageProperty` REFUSES an empty or whitespace-only value with a
 * ValidationError ("A property value cannot be empty. Remove the property
 * instead of clearing it.") rather than treating it as a delete, and
 * `removePageProperty` REFUSES a key the page doesn't carry, for the same
 * non-idempotent reason `detachLabel` does. Neither refusal describes a
 * mistake the *user* made — clearing a field is a perfectly ordinary way to
 * say "this page no longer has an owner" — so the translation happens here,
 * once, instead of a backend rule leaking into a toast.
 */
export type PropertyEditIntent =
  | { kind: 'set'; value: string }
  /** The cleared-field gesture: the row exists server-side, so removing it is what "clear" means. */
  | { kind: 'remove' }
  /** Unchanged, or cleared while never saved — nothing to send, and nothing the server would accept. */
  | { kind: 'none' }

/**
 * @param draft the value currently in the field
 * @param storedValue the value the server last reported, or null when this page carries no row for the key
 */
export function resolvePropertyEdit(draft: string, storedValue: string | null): PropertyEditIntent {
  // Whitespace-only counts as cleared because that is exactly the input the
  // server refuses; the value itself is sent untrimmed, since normalizing
  // what someone typed is the server's decision to make and it doesn't.
  const cleared = draft.trim().length === 0
  if (cleared) {
    return storedValue === null ? { kind: 'none' } : { kind: 'remove' }
  }
  if (draft === storedValue) return { kind: 'none' }
  return { kind: 'set', value: draft }
}

/** True when saving would send at least one mutation — the Save button's enabled state. */
export function hasPendingEdits(rows: { draft: string; storedValue: string | null }[]): boolean {
  return rows.some((row) => resolvePropertyEdit(row.draft, row.storedValue).kind !== 'none')
}
