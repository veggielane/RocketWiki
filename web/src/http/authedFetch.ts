import { getAccessToken } from '../graphql/authToken'

/**
 * Shared plumbing for the plain-HTTP binary routes (attachments §10,
 * avatars/emojis §19). These routes carry the same bearer token as GraphQL
 * (design.md §11) — read per request, because the token rotates in memory
 * — and had grown three byte-identical `authHeaders()` copies plus two
 * copies of the 413-ProblemDetails parsing. One home now.
 */
export function authHeaders(): HeadersInit {
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

/** A failed Response's JSON body (ProblemDetails-ish), or null when there isn't a parsable one. */
export async function readFailureBody(response: Response): Promise<Record<string, unknown> | null> {
  try {
    return (await response.json()) as Record<string, unknown>
  } catch {
    return null
  }
}

/**
 * The `maxSizeBytes` extension the API puts on 413 ProblemDetails —
 * server-declared so refusal copy can state the actual limit, not a guess.
 */
export function maxSizeBytesOf(body: Record<string, unknown> | null): number | null {
  return typeof body?.maxSizeBytes === 'number' ? body.maxSizeBytes : null
}
