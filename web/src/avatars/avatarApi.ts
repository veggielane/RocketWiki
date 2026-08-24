import { authHeaders, maxSizeBytesOf, readFailureBody } from '../http/authedFetch'

/**
 * Profile-picture routes (design.md §19) — plain HTTP binary, exactly the
 * attachment pattern (attachments/attachmentApi.ts): bytes stream through
 * the authenticated API with the bearer token, never a presigned URL and
 * never a bare `<img src="/users/{id}/avatar">` (a browser can't attach an
 * Authorization header to that request).
 *
 * `POST /avatars` / `DELETE /avatars` are structurally self-only — the
 * routes take no target user. The server normalizes every upload
 * (center-crop, resize to canonical 512, re-encode to PNG, metadata
 * stripped), so any client-side cropping is preview sugar: we always send
 * the user's original file.
 */

/** Formats and codes the server accepts for upload (§19); checked client-side only to fail fast with designed copy. */
export const AVATAR_ACCEPTED_TYPES = ['image/png', 'image/jpeg', 'image/webp']

/**
 * A refusal with designed copy, never a raw error toast: `tooLarge` carries
 * the server-declared cap from the 413 ProblemDetails' `maxSizeBytes`
 * extension so the message can state the actual limit, not a guess.
 */
export type AvatarUploadError = { kind: 'tooLarge'; maxSizeBytes: number | null } | { kind: 'refused'; message: string | null }

export class AvatarUploadFailure extends Error {
  readonly detail: AvatarUploadError

  constructor(detail: AvatarUploadError) {
    super(detail.kind)
    this.name = 'AvatarUploadFailure'
    this.detail = detail
  }
}

async function parseFailure(response: Response): Promise<AvatarUploadFailure> {
  const body = await readFailureBody(response)
  if (response.status === 413) {
    return new AvatarUploadFailure({ kind: 'tooLarge', maxSizeBytes: maxSizeBytesOf(body) })
  }
  const message = typeof body?.message === 'string' ? body.message : null
  return new AvatarUploadFailure({ kind: 'refused', message })
}

/** `POST /avatars` — multipart, single file field. Resolves to the server's `hasAvatar` echo. */
export async function uploadAvatar(file: File): Promise<{ hasAvatar: boolean }> {
  const formData = new FormData()
  formData.append('file', file)
  const response = await fetch('/avatars', { method: 'POST', headers: authHeaders(), body: formData })
  if (!response.ok) {
    throw await parseFailure(response)
  }
  return (await response.json()) as { hasAvatar: boolean }
}

/** `DELETE /avatars` — clears the acting user's avatar. */
export async function clearAvatar(): Promise<{ hasAvatar: boolean }> {
  const response = await fetch('/avatars', { method: 'DELETE', headers: authHeaders() })
  if (!response.ok) {
    throw await parseFailure(response)
  }
  return (await response.json()) as { hasAvatar: boolean }
}

/**
 * `GET /users/{id}/avatar` — the canonical 512 PNG, authenticated. Throws
 * on any non-OK status; callers fall back to initials without ever showing
 * the status to the user (an avatar-less user and a fetch failure look
 * identical, by design).
 */
export async function fetchAvatarBlob(userId: string): Promise<Blob> {
  const response = await fetch(`/users/${userId}/avatar`, { headers: authHeaders() })
  if (!response.ok) {
    throw new Error('avatar unavailable')
  }
  return response.blob()
}
