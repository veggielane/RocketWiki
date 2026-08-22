import { getAccessToken } from '../graphql/authToken'

/**
 * design.md §8/§10: attachment binary is a plain HTTP route, not GraphQL —
 * streaming large binaries doesn't belong in a GraphQL response. Same
 * bearer-token auth as every other channel (design.md §11).
 */
const ATTACHMENTS_BASE = '/attachments'

export interface UploadedAttachment {
  id: string
  fileName: string
  contentType: string
  sizeBytes: number
}

function authHeaders(): HeadersInit {
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

/** `POST /attachments/{pageId}` — multipart upload (design.md §8). */
export async function uploadAttachment(pageId: string, file: File): Promise<UploadedAttachment> {
  const formData = new FormData()
  formData.append('file', file)

  const response = await fetch(`${ATTACHMENTS_BASE}/${pageId}`, {
    method: 'POST',
    headers: authHeaders(),
    body: formData,
  })

  if (!response.ok) {
    throw new Error(`Upload failed (${response.status})`)
  }

  return (await response.json()) as UploadedAttachment
}

/**
 * `GET /attachments/{id}` — auth-checked, audited, then streamed
 * (design.md §8/§10). **No presigned URLs**: this fetch carries the bearer
 * token itself rather than a signed query-param URL, so a plain `<img
 * src="/attachments/{id}">` won't work in a browser (no way to attach a
 * header to that request) — see `useAttachmentBlobUrl` for how the editor
 * and attachment list actually consume this.
 */
export async function fetchAttachmentBlob(id: string): Promise<Blob> {
  const response = await fetch(`${ATTACHMENTS_BASE}/${id}`, { headers: authHeaders() })
  if (!response.ok) {
    // Deliberately the same error for "doesn't exist" and "exists but you
    // can't view it" — design.md §6.7's "absent rather than forbidden"
    // applies to attachments exactly like pages. Callers must not surface
    // `response.status` to the user.
    throw new Error('attachment unavailable')
  }
  return response.blob()
}
