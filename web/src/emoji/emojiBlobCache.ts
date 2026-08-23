import { fetchEmojiBlob } from './emojiApi'

/**
 * Blob-URL cache for emoji images, keyed by **(name, etag)** — the etag
 * comes from the `customEmojis` list and is the stored bytes' content
 * hash, immutable per registry entry (delete+recreate changes it). One
 * emoji can render hundreds of times across a document; this holds one
 * object URL per definition per session.
 *
 * When the list reports a new etag for a name, the stale entry is evicted
 * (object URL revoked — a leaked URL pins the decoded bytes) and the next
 * request refetches. The refetch itself rides the browser's HTTP cache,
 * where the server's strong ETag / If-None-Match → 304 handling lives; this
 * module only decides *when* to go back to the network at all.
 */

interface CacheEntry {
  etag: string
  promise: Promise<string | null>
  url: string | null
  settled: boolean
}

const cache = new Map<string, CacheEntry>()

function evict(name: string): void {
  const entry = cache.get(name)
  if (entry?.url) {
    URL.revokeObjectURL(entry.url)
  }
  cache.delete(name)
}

/** Resolves to a blob URL for the emoji, or null when it can't be fetched (deleted between list and render, etc.). */
export function getEmojiUrl(name: string, etag: string): Promise<string | null> {
  const existing = cache.get(name)
  if (existing) {
    if (existing.etag === etag) {
      return existing.promise
    }
    evict(name) // etag changed: the definition was replaced — bust and refetch
  }
  const entry: CacheEntry = { etag, url: null, settled: false, promise: Promise.resolve(null) }
  entry.promise = fetchEmojiBlob(name)
    .then((blob) => {
      entry.url = URL.createObjectURL(blob)
      entry.settled = true
      return entry.url
    })
    .catch(() => {
      // Cached miss: an unfetchable emoji renders as its literal text and
      // is not re-probed per render this session (unless the etag moves).
      entry.settled = true
      return null
    })
  cache.set(name, entry)
  return entry.promise
}

/** Synchronous peek at a resolved URL: string when ready, null for a settled miss, undefined while pending/unfetched or on etag mismatch. */
export function peekEmojiUrl(name: string, etag: string): string | null | undefined {
  const entry = cache.get(name)
  if (!entry || entry.etag !== etag || !entry.settled) return undefined
  return entry.url
}

/** Test-only: revoke and drop everything. */
export function resetEmojiBlobCache(): void {
  for (const name of [...cache.keys()]) {
    evict(name)
  }
}
