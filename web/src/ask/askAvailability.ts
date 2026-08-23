import { useSyncExternalStore } from 'react'

/**
 * Session-level "is the assistant even configured?" signal.
 *
 * Unlike GitLab there is no status query for the assistant — the schema's
 * only signal is the `askWiki` payload itself (`unavailable:
 * NOT_CONFIGURED`), so the gating is pragmatic: **the attempt is the
 * probe.** Affordances (app-bar entry, search-page nudge) render
 * optimistically until the first ask on this instance comes back
 * NOT_CONFIGURED; that result is cached here for the rest of the session
 * and every affordance collapses (§15/§18 fail-closed extends to
 * affordances, mirroring the GitLab treatment). The Ask page itself stays
 * routable and shows the honest feature-absent copy.
 *
 * NOT_CONFIGURED means no chat endpoint is set at all — deployment
 * configuration, not a transient state — so a session-lifetime cache never
 * re-probes; a fresh load after the instance is reconfigured sees the
 * feature again. UNREACHABLE and NO_RESULTS deliberately do NOT touch this
 * store: the feature exists, it just failed or found nothing this time.
 *
 * Module singleton + subscriber set (same shape as emoji/registry.ts) so
 * the AppShell button and any page can share it via `useSyncExternalStore`.
 */

let notConfigured = false
const subscribers = new Set<() => void>()

export function markAskWikiNotConfigured(): void {
  if (notConfigured) return
  notConfigured = true
  for (const notify of subscribers) notify()
}

export function isAskWikiMarkedNotConfigured(): boolean {
  return notConfigured
}

function subscribe(subscriber: () => void): () => void {
  subscribers.add(subscriber)
  return () => {
    subscribers.delete(subscriber)
  }
}

/**
 * True until this session has seen a NOT_CONFIGURED answer. "Possibly"
 * because absence of evidence is the steady state — there is no cheap
 * server signal to turn this into a definite yes.
 */
export function useAskWikiPossiblyAvailable(): boolean {
  return useSyncExternalStore(subscribe, () => !notConfigured)
}

/** Test-only reset. */
export function resetAskWikiAvailability(): void {
  notConfigured = false
  subscribers.clear()
}
