/**
 * Mirrors PagePropertyService's `MaxValueLength` / `MaxKeyLength` /
 * `MaxDescriptionLength` (design.md §20 — plain text, length is the only
 * validation there is). The server is the enforcement point, as always: these
 * exist so a field can fail fast with the *same* rule the server would state,
 * never so the client can decide the answer.
 */
export const MAX_PROPERTY_VALUE_LENGTH = 1000
export const MAX_PROPERTY_KEY_LENGTH = 64
export const MAX_PROPERTY_KEY_DESCRIPTION_LENGTH = 256
