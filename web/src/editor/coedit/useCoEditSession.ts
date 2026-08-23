import { useCallback, useEffect, useRef, useState } from 'react'
import type { CoEditTransport } from '../../realtime/types'
import { SignalRYjsProvider, type CoEditStatus } from './SignalRYjsProvider'
import { seedDocFromMarkdown } from './seedDoc'

export interface UseCoEditSessionResult {
  status: CoEditStatus
  /** Set once the join resolves into a session; null while connecting and in solo mode. */
  provider: SignalRYjsProvider | null
  /** The session's `expectedRevisionNumber` for the next save — from the join, advanced by saves/reseeds. */
  baseRevisionNumber: number | null
  /**
   * Non-null while the server has designated this client to save-and-reseed
   * (log_cap). The page performs the save and calls `completeReseed` with
   * the new revision number; a StaleRevision conflict leaves the demand
   * standing (any later successful session save should still complete it).
   */
  reseedDemand: number | null
  /** A single change too large for the relay's update cap — peers won't see it live; a save carries it. */
  oversizedUpdate: boolean
  clearOversizedUpdate: () => void
  /** A session save landed: advance the tracked base (the server advances its copy independently). */
  noteSaved: (newRevisionNumber: number) => void
  completeReseed: (newRevisionNumber: number) => Promise<void>
}

interface SessionState {
  status: CoEditStatus
  provider: SignalRYjsProvider | null
  baseRevisionNumber: number | null
  reseedDemand: number | null
  oversizedUpdate: boolean
}

const initialState = (key: string): SessionState => ({
  status: key === '' ? 'solo' : 'connecting',
  provider: null,
  baseRevisionNumber: null,
  reseedDemand: null,
  oversizedUpdate: false,
})

/**
 * The React face of `SignalRYjsProvider`: one provider per (pageId,
 * enabled) episode, disposed on route change and unmount — the same
 * "leaked hub subscription is a live data leak" rule presence follows
 * (design.md §8), except here the leak would be page CONTENT flowing to a
 * page the user has left, so the key-driven reset is even less optional.
 * The reset itself uses the render-time state-reset idiom (the same one
 * PageViewPage uses for route reuse); everything after that is written by
 * the provider's async callbacks, never synchronously from the effect.
 *
 * `seedMarkdown` is read through a ref at seed time, not a dependency: the
 * page's saved content at the moment the server designates us seeder is
 * what §8 asks for, and re-running the session because a refetch bumped
 * the string would tear down a live doc for nothing.
 */
export function useCoEditSession(
  pageId: string,
  options: { enabled: boolean; seedMarkdown: string; transport: CoEditTransport },
): UseCoEditSessionResult {
  const { enabled, transport } = options
  const key = enabled && pageId !== '' ? pageId : ''

  const seedMarkdownRef = useRef(options.seedMarkdown)
  useEffect(() => {
    seedMarkdownRef.current = options.seedMarkdown
  })

  const [state, setState] = useState<SessionState>(() => initialState(key))
  const [stateKey, setStateKey] = useState(key)
  if (stateKey !== key) {
    // Route reuse / enablement change: reset synchronously during render so
    // no frame ever shows page A's session state against page B.
    setStateKey(key)
    setState(initialState(key))
  }

  const providerRef = useRef<SignalRYjsProvider | null>(null)

  useEffect(() => {
    if (key === '') return

    let cancelled = false
    const guarded = (update: (previous: SessionState) => SessionState) => {
      if (!cancelled) setState(update)
    }

    const provider = new SignalRYjsProvider({
      pageId: key,
      transport,
      seed: (doc, origin) => seedDocFromMarkdown(doc, seedMarkdownRef.current, origin),
      // All of these fire asynchronously (after the join resolves or on
      // later hub events), so none of them set state during the effect.
      onStatusChange: (status) => guarded((previous) => ({ ...previous, status, provider })),
      onBaseRevisionChange: (baseRevisionNumber) => guarded((previous) => ({ ...previous, baseRevisionNumber })),
      onSaveAndReseedRequired: (base) => guarded((previous) => ({ ...previous, reseedDemand: base })),
      onOversizedUpdate: () => guarded((previous) => ({ ...previous, oversizedUpdate: true })),
    })
    providerRef.current = provider
    void provider.connect()

    return () => {
      cancelled = true
      providerRef.current = null
      provider.dispose()
    }
  }, [key, transport])

  const noteSaved = useCallback((newRevisionNumber: number) => {
    providerRef.current?.noteSaved(newRevisionNumber)
  }, [])

  const completeReseed = useCallback(async (newRevisionNumber: number) => {
    setState((previous) => ({ ...previous, reseedDemand: null }))
    await providerRef.current?.completeReseed(newRevisionNumber)
  }, [])

  const clearOversizedUpdate = useCallback(() => {
    setState((previous) => ({ ...previous, oversizedUpdate: false }))
  }, [])

  return {
    status: state.status,
    provider: state.provider,
    baseRevisionNumber: state.baseRevisionNumber,
    reseedDemand: state.reseedDemand,
    oversizedUpdate: state.oversizedUpdate,
    clearOversizedUpdate,
    noteSaved,
    completeReseed,
  }
}
