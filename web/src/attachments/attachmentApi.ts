import { authHeaders } from '../http/authedFetch'

/**
 * design.md §8/§10: attachment binary is a plain HTTP route, not GraphQL —
 * streaming large binaries doesn't belong in a GraphQL response. Same
 * bearer-token auth as every other channel (design.md §11), via the shared
 * http/authedFetch.ts helpers.
 */
const ATTACHMENTS_BASE = '/attachments'

/**
 * Every id that reaches these routes is interpolated through here.
 *
 * Neither id is typed by this app. `pageId` comes from the route, and — the
 * one that matters — the download id is whatever follows `attachment://` in
 * page Markdown (`editor/nodes/AttachmentImageView.tsx` slices the scheme off
 * and passes the rest through). Page content is written by other users, so an
 * author could store `![x](attachment://../../users/someone/avatar)` and every
 * reader's browser would issue an authenticated same-origin GET to that path.
 * Encoding keeps an id one path segment, whatever it contains — the same fix
 * the emoji routes already apply to their names.
 */
const segment = (value: string): string => encodeURIComponent(value)

export interface UploadedAttachment {
  id: string
  fileName: string
  contentType: string
  sizeBytes: number
}

/** `POST /attachments/{pageId}` — multipart upload (design.md §8). */
export async function uploadAttachment(pageId: string, file: File): Promise<UploadedAttachment> {
  const formData = new FormData()
  formData.append('file', file)

  const response = await fetch(`${ATTACHMENTS_BASE}/${segment(pageId)}`, {
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
  const response = await fetch(`${ATTACHMENTS_BASE}/${segment(id)}`, { headers: authHeaders() })
  if (!response.ok) {
    // Deliberately the same error for "doesn't exist" and "exists but you
    // can't view it" — design.md §6.7's "absent rather than forbidden"
    // applies to attachments exactly like pages. Callers must not surface
    // `response.status` to the user.
    throw new Error('attachment unavailable')
  }
  return response.blob()
}
