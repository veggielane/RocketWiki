import { useEffect } from 'react'
import { useLocation } from 'react-router-dom'

/**
 * The browser's native "scroll to #hash on load" only works if the target
 * element already exists at that moment — but the heading it needs to
 * find is rendered by TipTap after an async page fetch, so a plain
 * `<a href="#anchor">` link from search results would silently fail to
 * scroll on first navigation. Watches the DOM for the target id to show up
 * (rather than guessing a fixed delay) and scrolls to it once it does.
 */
export function useScrollToHash(readyDependency: unknown): void {
  const location = useLocation()

  useEffect(() => {
    const hash = location.hash.slice(1)
    if (!hash) return

    // Honours `prefers-reduced-motion`: a long smooth scroll to a deep heading
    // is precisely the kind of large involuntary movement the preference is
    // for. The destination is identical either way — only the journey changes.
    const behavior: ScrollBehavior = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
      ? 'auto'
      : 'smooth'

    const existing = document.getElementById(hash)
    if (existing) {
      existing.scrollIntoView({ behavior, block: 'start' })
      return
    }

    const observer = new MutationObserver(() => {
      const el = document.getElementById(hash)
      if (el) {
        el.scrollIntoView({ behavior, block: 'start' })
        observer.disconnect()
      }
    })
    observer.observe(document.body, { childList: true, subtree: true })

    // Give up after a few seconds rather than observing forever if the
    // anchor never appears (e.g. the heading was renamed since the search
    // index was last updated).
    const timeout = setTimeout(() => observer.disconnect(), 5000)

    return () => {
      observer.disconnect()
      clearTimeout(timeout)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [location.hash, readyDependency])
}
