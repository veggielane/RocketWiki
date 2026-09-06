import { useLayoutEffect, useRef, useState, type RefObject } from 'react'

export interface ElementSize {
  width: number
  height: number
}

/**
 * The rendered size of an element, kept current as it resizes.
 *
 * The canvases need explicit pixel dimensions (force-graph sizes its canvas
 * from props, not from CSS), and the container's size is decided by the
 * shell's measure and the viewport. Measured in a layout effect so the first
 * paint already has the real numbers, then followed by a ResizeObserver
 * where the runtime has one. jsdom has neither layout nor the observer, so
 * there the size stays 0×0 and the caller falls back to a nominal one.
 */
export function useElementSize<T extends HTMLElement>(): [RefObject<T | null>, ElementSize] {
  const ref = useRef<T | null>(null)
  const [size, setSize] = useState<ElementSize>({ width: 0, height: 0 })

  useLayoutEffect(() => {
    const element = ref.current
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
