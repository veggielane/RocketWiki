/**
 * Session-scoped blob-URL cache factory — the shared machinery behind
 * avatars/avatarCache.ts and emoji/emojiBlobCache.ts, which had grown the
 * same ~55 lines independently (CacheEntry/Map/objectURL/revoke/reset).
 * Those modules stay as the public seams — the preview harnesses
 * (preview/captureScreens.test.tsx, preview/a11yScreens.test.tsx) mock
 * them BY PATH with their exact export names, so this factory must never
 * become the import surface for components.
 *
 * Semantics (identical to what both copies implemented):
 *  - one fetch per key per session, however many mounts render it;
 *  - a resolved failure is cached as null — an unfetchable image degrades
 *    (initials / literal `:name:` text) and is not re-probed per render;
 *  - an optional per-key `version` (the emoji etag — a content hash from
 *    the registry list) busts the entry when it changes: stale URL
 *    revoked, next request refetches;
 *  - object URLs are owned by the cache and revoked on eviction/reset — a
 *    leaked object URL pins the decoded bytes for the page's lifetime.
 *
 * HTTP-level revalidation (strong ETag, max-age, If-None-Match → 304)
 * stays in the browser's cache; this module only decides *when* to go back
 * to the network at all.
 */

interface CacheEntry {
  version: string | undefined
  promise: Promise<string | null>
  /** Set once resolved — lets first paint be synchronous on later mounts. */
  url: string | null
  settled: boolean
}

export interface BlobUrlCache {
  /** Resolves to a blob URL, or null when the bytes can't be fetched. */
  get(key: string, version?: string): Promise<string | null>
  /** Synchronous peek: the URL, null for a settled miss, undefined while pending/unknown or on version mismatch. */
  peek(key: string, version?: string): string | null | undefined
  /** Evicts one key, revoking its object URL. */
  invalidate(key: string): void
  /** Drops everything, revoking every object URL. */
  reset(): void
}

export function createBlobUrlCache(fetcher: (key: string) => Promise<Blob>): BlobUrlCache {
  const cache = new Map<string, CacheEntry>()

  const evict = (key: string): void => {
    const entry = cache.get(key)
    if (entry?.url) {
      URL.revokeObjectURL(entry.url)
    }
    cache.delete(key)
  }

  return {
    get(key, version) {
      const existing = cache.get(key)
      if (existing) {
        if (existing.version === version) {
          return existing.promise
        }
        evict(key) // version changed: the definition was replaced — bust and refetch
      }
      const entry: CacheEntry = { version, url: null, settled: false, promise: Promise.resolve(null) }
      entry.promise = fetcher(key)
        .then((blob) => {
          entry.url = URL.createObjectURL(blob)
          entry.settled = true
          return entry.url
        })
        .catch(() => {
          // 404 and transport failure collapse to the same cached miss —
          // the caller's fallback rendering, not re-probed this session.
          entry.settled = true
          return null
        })
      cache.set(key, entry)
      return entry.promise
    },

    peek(key, version) {
      const entry = cache.get(key)
      if (!entry || entry.version !== version || !entry.settled) return undefined
      return entry.url
    },

    invalidate(key) {
      evict(key)
    },

    reset() {
      for (const key of [...cache.keys()]) {
        evict(key)
      }
    },
  }
}
