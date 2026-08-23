import { getAccessToken } from '../graphql/authToken'

/**
 * Custom-emoji binary routes (design.md §19), following the attachment/
 * avatar pattern: authenticated fetch + blob URLs, never a direct
 * `<img src>` to the API. The admin mutations POST **raw bytes** (one
 * file, one route — no multipart envelope) and surface the flattened
 * `PageMutationErrorView` shape (kind/message) the routes return, plus the
 * ProblemDetails 413 with its `maxSizeBytes` extension.
 */

function authHeaders(): HeadersInit {
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

/** Upload formats the server accepts (it re-encodes; animated GIF survives frame-preserving). */
export const EMOJI_ACCEPTED_TYPES = ['image/png', 'image/jpeg', 'image/webp', 'image/gif']

export type EmojiMutationError =
  | { kind: 'tooLarge'; maxSizeBytes: number | null }
  | { kind: 'nameTaken'; message: string | null }
  | { kind: 'forbidden'; message: string | null }
  | { kind: 'refused'; message: string | null }

export class EmojiMutationFailure extends Error {
  readonly detail: EmojiMutationError

  constructor(detail: EmojiMutationError) {
    super(detail.kind)
    this.name = 'EmojiMutationFailure'
    this.detail = detail
  }
}

async function parseFailure(response: Response): Promise<EmojiMutationFailure> {
  let body: Record<string, unknown> | null = null
  try {
    body = (await response.json()) as Record<string, unknown>
  } catch {
    body = null
  }
  if (response.status === 413) {
    const max = typeof body?.maxSizeBytes === 'number' ? body.maxSizeBytes : null
    return new EmojiMutationFailure({ kind: 'tooLarge', maxSizeBytes: max })
  }
  const message = typeof body?.message === 'string' ? body.message : null
  const kind = typeof body?.kind === 'string' ? body.kind : null
  if (kind === 'NameTaken') return new EmojiMutationFailure({ kind: 'nameTaken', message })
  if (kind === 'Forbidden') return new EmojiMutationFailure({ kind: 'forbidden', message })
  return new EmojiMutationFailure({ kind: 'refused', message })
}

export interface CreatedEmoji {
  name: string
  contentType: string
  sizeBytes: number
  pixelSize: number
  etag: string
}

/** `POST /emojis/{name}` — raw image bytes as the request body, instance-admin only. */
export async function uploadEmoji(name: string, file: File): Promise<CreatedEmoji> {
  const response = await fetch(`/emojis/${encodeURIComponent(name)}`, {
    method: 'POST',
    headers: { ...authHeaders(), 'Content-Type': file.type || 'application/octet-stream' },
    body: file,
  })
  if (!response.ok) {
    throw await parseFailure(response)
  }
  return (await response.json()) as CreatedEmoji
}

/** `DELETE /emojis/{name}` — 204 on success; a hard delete (content keeps rendering the literal text, harmlessly). */
export async function deleteEmoji(name: string): Promise<void> {
  const response = await fetch(`/emojis/${encodeURIComponent(name)}`, {
    method: 'DELETE',
    headers: authHeaders(),
  })
  if (response.ok) return
  if (response.status === 404) {
    // Absent already — the caller's goal state. Refreshing the list is the
    // only follow-up either way.
    return
  }
  throw await parseFailure(response)
}

/**
 * `GET /emojis/{name}` — the stored bytes (image/png or image/gif),
 * authenticated. The server's strong ETag + max-age headers make repeat
 * fetches cheap at the HTTP layer; the session-level dedupe lives in
 * emojiBlobCache.ts.
 */
export async function fetchEmojiBlob(name: string): Promise<Blob> {
  const response = await fetch(`/emojis/${encodeURIComponent(name)}`, { headers: authHeaders() })
  if (!response.ok) {
    throw new Error('emoji unavailable')
  }
  return response.blob()
}
