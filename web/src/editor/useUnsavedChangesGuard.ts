import { useEffect } from 'react'
import { useBlocker } from 'react-router-dom'

/**
 * Stops an in-progress edit from being thrown away by a click.
 *
 * The editor had no guard at all: Cancel navigated immediately, and so did the
 * sidebar tree, the breadcrumb, the search box, the notification bell, the theme
 * toggle and the browser's own back button — every one of them live while
 * editing. For a wiki that is data loss, not a polish item.
 *
 * Two mechanisms because they cover different exits and neither covers both:
 *
 * - `useBlocker` catches navigation INSIDE the app. It needs a data router,
 *   which app/router.tsx is. The blocker is returned so the caller can render
 *   its own dialog rather than a `window.confirm` — this app routes designed
 *   failure modes through dialogs (web/README.md), and a leave-confirmation is
 *   one.
 * - `beforeunload` catches leaving the app entirely (tab close, reload, an
 *   external link). The browser owns that dialog and its wording; we can only
 *   ask for it, and only while there is genuinely something to lose — a
 *   handler registered unconditionally would prompt on every reload of a page
 *   nobody touched, which trains people to click through it.
 *
 * Two parameters for one fact because they are read at different times:
 * `hasUnsavedChanges` is called at navigation time and must be current to the
 * instant (see the comment on the blocker), while `dirty` is ordinary render
 * state and is what decides whether the `beforeunload` listener is registered
 * at all.
 */
export function useUnsavedChangesGuard(hasUnsavedChanges: () => boolean, dirty: boolean) {
  const blocker = useBlocker(({ currentLocation, nextLocation }) => {
    // Read through the CALLER'S predicate, not a captured boolean.
    //
    // A save clears the dirty flag and navigates in the same handler. React
    // batches the state update, so a blocker closed over `dirty` would still
    // see `true` at the moment `navigate()` runs and pop "Leave without
    // saving?" on a successful save — which is both wrong and the exact
    // opposite of what the guard is for. A predicate reading a ref sees the
    // clear immediately.
    if (!hasUnsavedChanges()) return false
    // A hash-only change is scrolling to a heading, not leaving.
    return currentLocation.pathname !== nextLocation.pathname
  })

  useEffect(() => {
    if (!dirty) return
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      // Assigning returnValue is what actually triggers the prompt in older
      // engines; `preventDefault` alone is the modern spelling and Chromium
      // wants both.
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', onBeforeUnload)
    return () => window.removeEventListener('beforeunload', onBeforeUnload)
  }, [dirty])

  return blocker
}
