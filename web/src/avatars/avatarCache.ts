import { fetchAvatarBlob } from './avatarApi'

/**
 * Session-scoped avatar blob-URL cache, keyed by user id: **one fetch per
 * user per session**, however many comment rows or presence chips render
 * that face. A page of 40 comments by 3 authors costs 3 GETs, not 40.
 *
 * ETag semantics live at the HTTP layer, not here: the server puts a strong
 * ETag + `private, max-age=300` on the route, so the browser's HTTP cache
 * absorbs revalidation whenever this module does refetch. Within a session
 * this cache never revalidates on its own — the render decision comes from
 * `hasAvatar` on the GraphQL side, and the only in-session change we must
 * reflect is the *viewer's own* upload/clear, which goes through
 * `invalidateAvatar` below.
 *
 * A resolved `null` means "no avatar" (404) and is cached too — the
 * graceful-fallback path (presence, where the wire payload has no
 * `hasAvatar`) must not re-probe per render. Object URLs are revoked on
 * eviction (invalidate/reset): a leaked object URL pins the decoded bytes
 * for the page's lifetime.
 */

interface CacheEntry {
  promise: Promise<string | null>
  /** Set once resolved — lets first paint be synchronous on later mounts. */
  url: string | null
  settled: boolean
}

const cache = new Map<string, CacheEntry>()

/** Resolves to a blob URL, or null when the user has no fetchable avatar. */
export function getAvatarUrl(userId: string): Promise<string | null> {
  const existing = cache.get(userId)
  if (existing) {
    return existing.promise
  }
  const entry: CacheEntry = { url: null, settled: false, promise: Promise.resolve(null) }
  entry.promise = fetchAvatarBlob(userId)
    .then((blob) => {
      entry.url = URL.createObjectURL(blob)
      entry.settled = true
      return entry.url
    })
    .catch(() => {
      // 404 (no avatar / shadow user) and transport failure collapse to the
      // same initials fallback — cached so nothing re-probes this session.
      entry.settled = true
      return null
    })
  cache.set(userId, entry)
  return entry.promise
}

/** Synchronous peek: the resolved URL, null for "resolved: no avatar", undefined while unknown/pending. */
export function peekAvatarUrl(userId: string): string | null | undefined {
  const entry = cache.get(userId)
  if (!entry || !entry.settled) return undefined
  return entry.url
}

/** Evicts one user (the viewer, after uploading or clearing their own avatar), revoking the object URL. */
export function invalidateAvatar(userId: string): void {
  const entry = cache.get(userId)
  if (entry?.url) {
    URL.revokeObjectURL(entry.url)
  }
  cache.delete(userId)
}

/** Test-only: drop everything, revoking every object URL. */
export function resetAvatarCache(): void {
  for (const entry of cache.values()) {
    if (entry.url) URL.revokeObjectURL(entry.url)
  }
  cache.clear()
}
