/**
 * The in-memory emoji registry: name → etag, fed from the generated
 * `CustomEmojis` GraphQL query (useEmojiRegistry.ts) and read synchronously
 * by everything that can't await — the ProseMirror decoration plugin, the
 * suggestion filter, candidate matching. A module singleton rather than
 * React context because the decoration plugin runs outside React entirely.
 *
 * Subscribers are notified on change so live surfaces (open editors) can
 * re-decorate when the list arrives or an admin adds/removes a name.
 */

export interface EmojiRegistryEntry {
  name: string
  /** The stored bytes' content-hash ETag from `customEmojis { etag }` — the blob cache's freshness key. */
  etag: string
}

let entries = new Map<string, string>()
let version = 0
const subscribers = new Set<() => void>()

export function setEmojiRegistry(list: readonly EmojiRegistryEntry[]): void {
  const next = new Map(list.map((e) => [e.name, e.etag]))
  // Cheap change detection: same size and every (name, etag) pair equal.
  if (next.size === entries.size && [...next].every(([name, etag]) => entries.get(name) === etag)) {
    return
  }
  entries = next
  version += 1
  for (const notify of subscribers) notify()
}

/** name → etag. Callers must treat it as read-only. */
export function getEmojiRegistry(): ReadonlyMap<string, string> {
  return entries
}

export function isKnownEmoji(name: string): boolean {
  return entries.has(name)
}

/** Monotonic change counter — a cheap dependency key for React effects. */
export function getEmojiRegistryVersion(): number {
  return version
}

export function subscribeEmojiRegistry(subscriber: () => void): () => void {
  subscribers.add(subscriber)
  return () => {
    subscribers.delete(subscriber)
  }
}

/** Test-only reset. */
export function resetEmojiRegistry(): void {
  entries = new Map()
  version = 0
  subscribers.clear()
}
