import { useCallback, useState, type RefCallback } from 'react'

export interface ElementSize {
  width: number
  height: number
}

/**
 * The rendered size of an element, kept current as it resizes.
 *
 * The canvases need explicit pixel dimensions (force-graph sizes its canvas
 * from props, not from CSS), and the container's size is decided by the
 * shell's layout and the viewport. Measured the moment the element is
 * attached, so the first paint already has the real numbers, then followed by
 * a ResizeObserver where the runtime has one. jsdom has neither layout nor
 * the observer, so there the size stays 0×0 and the caller falls back to a
 * nominal one.
 *
 * A CALLBACK ref, not a ref object read in a mount effect. The element this
 * measures is not there on the first render — the graph page shows a skeleton
 * until its query answers, and only then mounts the canvas box — and a mount
 * effect that found `ref.current` null had nothing to observe and no reason to
 * run again. Every canvas the feature drew was the fallback size, at every
 * viewport. The callback runs when the element is attached, whenever that is,
 * and its cleanup runs when it is detached, so the observer never outlives what
 * it watches.
 */
export function useElementSize<T extends HTMLElement>(): [RefCallback<T>, ElementSize] {
  const [size, setSize] = useState<ElementSize>({ width: 0, height: 0 })

  // Identity-stable: React re-runs a callback ref (cleanup, then attach) when
  // its identity changes, and that would re-measure on every render.
  const ref = useCallback<RefCallback<T>>((element) => {
    if (!element) return
    const measure = () => {
      const rect = element.getBoundingClientRect()
      const next = { width: Math.round(rect.width), height: Math.round(rect.height) }
      setSize((prev) => (prev.width === next.width && prev.height === next.height ? prev : next))
    }
    measure()
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(measure)
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  return [ref, size]
}
