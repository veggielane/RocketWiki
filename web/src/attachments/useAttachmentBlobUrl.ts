import { useEffect, useState } from 'react'
import { fetchAttachmentBlob } from './attachmentApi'

export type AttachmentBlobState = { status: 'loading' } | { status: 'ready'; url: string } | { status: 'error' }

function initialStateFor(id: string | null | undefined): AttachmentBlobState {
  return id ? { status: 'loading' } : { status: 'error' }
}

/**
 * Turns an attachment id into a displayable image `src` without ever
 * exposing an unauthenticated URL: fetches the bytes through the
 * authenticated API (attachmentApi.ts — no presigned URLs, design.md §10)
 * and wraps them in a same-origin `blob:` object URL, which *can* go
 * straight into an `<img src>` since the auth already happened during the
 * fetch, not at render time.
 *
 * Revokes the object URL on unmount/id-change — leaking these is a real
 * memory leak (the browser holds the decoded bytes until revoked), not
 * just an untidy habit.
 */
export function useAttachmentBlobUrl(id: string | null | undefined): AttachmentBlobState {
  const [trackedId, setTrackedId] = useState(id)
  const [state, setState] = useState<AttachmentBlobState>(() => initialStateFor(id))

  // Reset to loading/error synchronously during render when `id` changes,
  // rather than via a setState call inside the effect below — this is
  // React's documented "adjusting state when a prop changes" pattern
  // (avoids an extra commit versus resetting from inside the effect).
  if (id !== trackedId) {
    setTrackedId(id)
    setState(initialStateFor(id))
  }

  useEffect(() => {
    if (!id) {
      return
    }

    let cancelled = false
    let objectUrl: string | null = null

    fetchAttachmentBlob(id)
      .then((blob) => {
        if (cancelled) return
        objectUrl = URL.createObjectURL(blob)
        setState({ status: 'ready', url: objectUrl })
      })
      .catch(() => {
        if (!cancelled) {
          setState({ status: 'error' })
        }
      })

    return () => {
      cancelled = true
      if (objectUrl) {
        URL.revokeObjectURL(objectUrl)
      }
    }
  }, [id])

  return state
}
