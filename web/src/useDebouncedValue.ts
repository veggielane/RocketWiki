import { useEffect, useState } from 'react'

/**
 * Trails `value` by `delayMs`, resetting the wait on every change.
 *
 * Written for the live fence previews (nodes/CodeBlockView.tsx): while
 * typing in a gitlab fence, wait for the keystrokes to settle before
 * re-parsing — and therefore before re-fetching, since each variables
 * change is a live API call. Read mode passes 0, because the source is
 * static and the preview should be there on first paint.
 *
 * It lived in `editor/` while the editor was its only caller, with a note
 * saying it would move the day a second area wanted it. The search box is that
 * second caller (it debounces the query behind the URL), so it has moved — to
 * the top of `src/` rather than into a `hooks/` bucket, which web/README.md
 * rules out precisely because it would give every future file two plausible
 * homes. Nothing here belongs to any one feature, so no feature folder owns it.
 */
export function useDebouncedValue<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    if (delayMs === 0) return
    const timer = setTimeout(() => setDebounced(value), delayMs)
    return () => clearTimeout(timer)
  }, [value, delayMs])
  // Zero delay derives directly — no state write, no extra render.
  return delayMs === 0 ? value : debounced
}
