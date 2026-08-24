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
 * Lives beside its callers rather than in a generic bucket: the repo files
 * a hook under the folder whose feature owns it (presence/usePresence.ts,
 * auth/useIsInstanceAdmin.ts, attachments/useAttachmentBlobUrl.ts). Nothing
 * about it is editor-specific, so it moves again the day a second area
 * wants it.
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
