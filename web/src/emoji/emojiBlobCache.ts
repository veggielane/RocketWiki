import { createBlobUrlCache } from '../http/blobUrlCache'
import { fetchEmojiBlob } from './emojiApi'

/**
 * Blob-URL cache for emoji images, keyed by **(name, etag)** — the etag
 * comes from the `customEmojis` list and is the stored bytes' content
 * hash, immutable per registry entry (delete+recreate changes it). One
 * emoji can render hundreds of times across a document; this holds one
 * object URL per definition per session. When the list reports a new etag
 * for a name, the stale entry is evicted and the next request refetches.
 *
 * The mechanics (dedupe, cached miss, version busting, URL revocation)
 * live in http/blobUrlCache.ts; this module stays the import surface — the
 * preview harnesses mock it by path with exactly these export names.
 */
const cache = createBlobUrlCache(fetchEmojiBlob)

/** Resolves to a blob URL for the emoji, or null when it can't be fetched (deleted between list and render, etc.). */
export function getEmojiUrl(name: string, etag: string): Promise<string | null> {
  return cache.get(name, etag)
}

/** Synchronous peek at a resolved URL: string when ready, null for a settled miss, undefined while pending/unfetched or on etag mismatch. */
export function peekEmojiUrl(name: string, etag: string): string | null | undefined {
  return cache.peek(name, etag)
}

/** Test-only: revoke and drop everything. */
export function resetEmojiBlobCache(): void {
  cache.reset()
}
