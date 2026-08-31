/**
 * The spaces you have been in lately, most recent first.
 *
 * **Order only.** This list is a sequence of keys and nothing else — no names,
 * no ids, no membership claim. Whether a key still names a space you may see is
 * decided every render by intersecting it with the viewable space list the rail
 * already holds (see `RecentSpaces`), so a space you have lost access to simply
 * stops appearing. That split is deliberate: a cache that also remembered names
 * would be a second, stale copy of who may see what, and §6.7 says a space you
 * cannot see must be indistinguishable from one that does not exist.
 *
 * A module store rather than a plain read of `localStorage`, for the same
 * reason the emoji registry is one: the rail has to re-render when the list
 * changes, and a bare `localStorage.getItem` in a component is a value React
 * has no way to know went stale. `useSyncExternalStore` over this is how the
 * rail stays correct as you navigate between spaces.
 *
 * Client-side and per-device by design — no query, because a rail element
 * renders on every route and this needs no per-navigation round trip.
 */

const STORAGE_KEY = 'rocketwiki:recent-spaces'

/**
 * How many keys are kept. Deliberately more than are shown: five is what fits
 * the rail, but a sixth remembered space is what makes the list still useful
 * after you visit somewhere new.
 */
export const RECENT_SPACES_STORED_CAP = 10

/** How many the rail shows. */
export const RECENT_SPACES_SHOWN = 5

let cached: readonly string[] | null = null
const listeners = new Set<() => void>()

function read(): readonly string[] {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    if (raw === null) return []
    const parsed: unknown = JSON.parse(raw)
    // Anything that is not a list of strings is treated as absent rather than
    // repaired: this is a convenience cache, and a half-understood one is worth
    // less than an empty one.
    if (!Array.isArray(parsed)) return []
    return parsed.filter((entry): entry is string => typeof entry === 'string').slice(0, RECENT_SPACES_STORED_CAP)
  } catch {
    // Storage can be absent or throw outright (private modes, blocked
    // third-party contexts), and JSON.parse can throw on anything. No recent
    // spaces is a fine answer; an exception in the rail is not.
    return []
  }
}

function write(keys: readonly string[]): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(keys))
  } catch {
    // Remembering is a nicety. Failing to remember must not fail a navigation.
  }
}

/** The current list, with a stable identity so `useSyncExternalStore` does not loop. */
export function getRecentSpaceKeys(): readonly string[] {
  cached ??= read()
  return cached
}

export function subscribeToRecentSpaces(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

/**
 * Records a visit, moving the key to the front.
 *
 * Compared case-insensitively, because a space key is: `/spaces/eng` and
 * `/spaces/ENG` are one space, and storing both would spend two of five slots
 * on it and show it twice. The key is stored as given — callers pass the
 * server's canonical spelling, which is what the rail displays against.
 */
export function recordSpaceVisit(spaceKey: string): void {
  const trimmed = spaceKey.trim()
  if (trimmed.length === 0) return
  const previous = getRecentSpaceKeys()
  const withoutIt = previous.filter((key) => key.toLowerCase() !== trimmed.toLowerCase())
  const next = [trimmed, ...withoutIt].slice(0, RECENT_SPACES_STORED_CAP)
  // Nothing changed: re-recording the space you are already in must not
  // re-render the rail on every navigation within it.
  if (next.length === previous.length && next.every((key, i) => key === previous[i])) return
  cached = next
  write(next)
  for (const listener of listeners) listener()
}

/**
 * Forgets everything, for sign-out.
 *
 * A list of space keys is not a token, but it is a record of where somebody has
 * been, and on a shared workstation it would outlive them — a key like
 * `OPBLACKSTAR` tells the next person at that browser that such a programme
 * exists. Signing out is an explicit "I am done here", so it is the honest
 * moment to drop it.
 */
export function clearRecentSpaces(): void {
  cached = []
  try {
    window.localStorage.removeItem(STORAGE_KEY)
  } catch {
    // Nothing to do — the in-memory copy is cleared either way.
  }
  for (const listener of listeners) listener()
}

/** Test only: drops the in-memory copy so the next read comes from storage. */
export function resetRecentSpacesCache(): void {
  cached = null
}
