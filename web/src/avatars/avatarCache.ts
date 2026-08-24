import { createBlobUrlCache } from '../http/blobUrlCache'
import { fetchAvatarBlob } from './avatarApi'

/**
 * Session-scoped avatar blob-URL cache, keyed by user id: **one fetch per
 * user per session**, however many comment rows or presence chips render
 * that face. A page of 40 comments by 3 authors costs 3 GETs, not 40.
 *
 * The mechanics (dedupe, cached miss, URL revocation on eviction) live in
 * http/blobUrlCache.ts; this module stays the import surface — the preview
 * harnesses mock it by path with exactly these export names.
 *
 * ETag semantics live at the HTTP layer, not here: the server puts a strong
 * ETag + `private, max-age=300` on the route, so the browser's HTTP cache
 * absorbs revalidation whenever this module does refetch. Within a session
 * this cache never revalidates on its own — the render decision comes from
 * `hasAvatar` on the GraphQL side, and the only in-session change we must
 * reflect is the *viewer's own* upload/clear, which goes through
 * `invalidateAvatar` below. A resolved `null` means "no avatar" (404) and
 * is cached too — the graceful-fallback path (presence, where the wire
 * payload has no `hasAvatar`) must not re-probe per render.
 */
const cache = createBlobUrlCache(fetchAvatarBlob)

/** Resolves to a blob URL, or null when the user has no fetchable avatar. */
export function getAvatarUrl(userId: string): Promise<string | null> {
  return cache.get(userId)
}

/** Synchronous peek: the resolved URL, null for "resolved: no avatar", undefined while unknown/pending. */
export function peekAvatarUrl(userId: string): string | null | undefined {
  return cache.peek(userId)
}

/** Evicts one user (the viewer, after uploading or clearing their own avatar), revoking the object URL. */
export function invalidateAvatar(userId: string): void {
  cache.invalidate(userId)
}

/** Test-only: drop everything, revoking every object URL. */
export function resetAvatarCache(): void {
  cache.reset()
}
