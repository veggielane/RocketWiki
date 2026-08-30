import { useCallback, useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useDebouncedValue } from '../useDebouncedValue'

/**
 * A single query-string parameter, read and written as ordinary component state.
 *
 * The point is deep-linking. A filtered view that lives only in component state
 * cannot be bookmarked, shared, reloaded, or reached with Back — which is most
 * of what someone does with a filtered audit view. `SearchPage` already put its
 * whole query in the URL for exactly this reason; this is that idiom extracted
 * so the next screen does not re-derive it.
 *
 * Writes with `replace`, so changing a filter is not a history entry per change.
 */
export function useSearchParamState(name: string): [string, (value: string) => void] {
  const [params, setParams] = useSearchParams()
  const value = params.get(name) ?? ''

  const setValue = useCallback(
    (next: string) => {
      setParams(
        (current) => {
          const updated = new URLSearchParams(current)
          if (next) updated.set(name, next)
          else updated.delete(name)
          return updated
        },
        { replace: true },
      )
    },
    [name, setParams],
  )

  return [value, setValue]
}

/**
 * The same, for a field someone TYPES into: the input stays instant while the
 * URL — and therefore the request — trails behind by `delayMs`.
 *
 * Returns the draft (what the field shows) and the committed value (what the
 * URL holds, and what a query should be built from). They differ mid-word, and
 * a caller that builds its variables from the draft has not debounced anything.
 *
 * The `lastWritten` ref is what distinguishes "our debounce settled, push it" from
 * "the URL moved under us, adopt it" — a distinction no comparison of the two
 * values can make on its own, and whose absence made SearchPage's two effects
 * fight each other over Back/Forward.
 */
export function useDebouncedSearchParam(
  name: string,
  delayMs: number,
): [draft: string, setDraft: (value: string) => void, committed: string] {
  const [committed, setCommitted] = useSearchParamState(name)
  const [draft, setDraft] = useState(committed)
  const debounced = useDebouncedValue(draft, delayMs)
  const lastWritten = useRef(committed)

  // Settled draft → URL.
  useEffect(() => {
    if (debounced !== draft) return // still settling: mid-word
    if (committed !== lastWritten.current) return // the URL moved; the effect below owns this
    if (debounced === committed) return
    lastWritten.current = debounced
    setCommitted(debounced)
  }, [debounced, draft, committed, setCommitted])

  // URL → field, when the change came from anywhere but us (Back/Forward, a
  // "clear all", a pasted link).
  useEffect(() => {
    if (committed === lastWritten.current) return
    lastWritten.current = committed
    setDraft(committed)
  }, [committed])

  return [draft, setDraft, committed]
}
