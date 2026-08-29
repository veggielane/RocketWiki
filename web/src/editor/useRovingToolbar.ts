import { useCallback, useEffect, useRef, type KeyboardEvent, type RefObject } from 'react'

/** Enabled controls inside the toolbar, in DOM order. Menus render in a portal, so they are never here. */
function itemsIn(container: HTMLElement | null): HTMLButtonElement[] {
  if (!container) return []
  return Array.from(container.querySelectorAll<HTMLButtonElement>('button:not([disabled])'))
}

/**
 * The `role="toolbar"` keyboard contract (WAI-ARIA Authoring Practices).
 *
 * A toolbar is ONE tab stop: Tab moves into it and straight out again, and
 * arrow keys move between the controls inside. The editor toolbar declared the
 * role without implementing any of that, so every one of its ~20 buttons was an
 * independent tab stop and a keyboard user tabbing toward the editing surface
 * had to pass through all of them — more once a table selection revealed the
 * contextual group. axe cannot check this: the role is correct, the labels are
 * correct, and the keyboard behaviour it implies is simply absent.
 *
 * The active index lives in a ref and is written straight to the DOM rather than
 * held in state. Re-rendering the toolbar on every arrow key would rebuild the
 * button list mid-navigation, and the list already changes shape underneath us
 * whenever the selection enters or leaves a table.
 */
export function useRovingToolbar(containerRef: RefObject<HTMLElement | null>): {
  onKeyDown: (event: KeyboardEvent<HTMLElement>) => void
} {
  const activeIndex = useRef(0)

  const applyTabStops = useCallback(() => {
    const items = itemsIn(containerRef.current)
    if (items.length === 0) return
    // A control can disappear (the table group folds away when the caret
    // leaves a table), so the remembered index is clamped rather than trusted.
    if (activeIndex.current >= items.length) activeIndex.current = items.length - 1
    items.forEach((item, index) => {
      item.tabIndex = index === activeIndex.current ? 0 : -1
    })
  }, [containerRef])

  // Re-run on every render: the toolbar's control set is contextual, and a
  // newly mounted button defaults to tabIndex 0, which would quietly give the
  // toolbar a second tab stop.
  useEffect(applyTabStops)

  // Clicking a control makes it the one Tab returns to — otherwise focus would
  // jump back to whichever button happened to be first.
  useEffect(() => {
    const container = containerRef.current
    if (!container) return
    const onFocusIn = (event: FocusEvent) => {
      const items = itemsIn(container)
      const index = items.indexOf(event.target as HTMLButtonElement)
      if (index >= 0) {
        activeIndex.current = index
        applyTabStops()
      }
    }
    container.addEventListener('focusin', onFocusIn)
    return () => container.removeEventListener('focusin', onFocusIn)
  }, [containerRef, applyTabStops])

  const onKeyDown = useCallback(
    (event: KeyboardEvent<HTMLElement>) => {
      const items = itemsIn(containerRef.current)
      if (items.length === 0) return
      const current = items.indexOf(document.activeElement as HTMLButtonElement)
      if (current < 0) return

      let next: number
      switch (event.key) {
        case 'ArrowRight':
          // Wrapping, as the practices specify: the toolbar is a ring, and
          // arrowing off the end being a dead stop reads as a broken key.
          next = (current + 1) % items.length
          break
        case 'ArrowLeft':
          next = (current - 1 + items.length) % items.length
          break
        case 'Home':
          next = 0
          break
        case 'End':
          next = items.length - 1
          break
        default:
          return
      }

      event.preventDefault()
      activeIndex.current = next
      applyTabStops()
      items[next]?.focus()
    },
    [containerRef, applyTabStops],
  )

  return { onKeyDown }
}
