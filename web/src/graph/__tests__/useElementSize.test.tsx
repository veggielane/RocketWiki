import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, render, screen } from '@testing-library/react'
import { useElementSize } from '../useElementSize'

/**
 * The hook measures whatever element its ref lands on, and keeps measuring
 * it. The case that matters most is the one the graph page actually has: the
 * element is NOT there on the first render — it sits behind a loading
 * skeleton until the query answers — and only appears later. A hook that
 * measured once on mount saw nothing, never looked again, and every canvas
 * the feature ever drew was the nominal fallback size. So the first test
 * below mounts with the element absent and then makes it appear; a test that
 * mounted with it already present would have passed all along.
 */

function Probe({ show }: { show: boolean }) {
  const [ref, size] = useElementSize<HTMLDivElement>()
  return (
    <>
      <output data-testid="size">{`${size.width}x${size.height}`}</output>
      {show && <div ref={ref} data-testid="box" />}
    </>
  )
}

const size = () => screen.getByTestId('size').textContent

/** jsdom lays nothing out; this is what the box "is" for the duration of a test. */
function layOutBoxesAt(width: number, height: number) {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
    const measured = this.dataset['testid'] === 'box' ? { width, height } : { width: 0, height: 0 }
    return { ...measured, top: 0, left: 0, right: measured.width, bottom: measured.height, x: 0, y: 0, toJSON: () => ({}) }
  })
}

/** A ResizeObserver stand-in that records what it was asked to watch and lets a test raise a resize. */
class FakeResizeObserver {
  static instances: FakeResizeObserver[] = []
  observed: Element[] = []
  disconnected = false
  private readonly callback: ResizeObserverCallback
  constructor(callback: ResizeObserverCallback) {
    this.callback = callback
    FakeResizeObserver.instances.push(this)
  }
  observe(element: Element) {
    this.observed.push(element)
  }
  unobserve() {}
  disconnect() {
    this.disconnected = true
  }
  fire() {
    this.callback([], this as unknown as ResizeObserver)
  }
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  FakeResizeObserver.instances = []
})

describe('useElementSize', () => {
  it('measures an element that appears after the first render', () => {
    layOutBoxesAt(1110, 640)
    const { rerender } = render(<Probe show={false} />)
    expect(size()).toBe('0x0')

    rerender(<Probe show />)
    expect(size()).toBe('1110x640')
  })

  it('measures an element that is there from the start', () => {
    layOutBoxesAt(800, 300)
    render(<Probe show />)
    expect(size()).toBe('800x300')
  })

  it('reports 0×0 where nothing has a layout, which is what the callers fall back from', () => {
    // jsdom's own getBoundingClientRect: every rect is 0×0, and there is no
    // ResizeObserver. The graph page turns that into its nominal size.
    expect(typeof ResizeObserver).toBe('undefined')
    render(<Probe show />)
    expect(size()).toBe('0x0')
  })

  it('follows the element as it resizes, and stops watching it once it is gone', () => {
    vi.stubGlobal('ResizeObserver', FakeResizeObserver)
    let width = 1110
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      const w = this.dataset['testid'] === 'box' ? width : 0
      return { width: w, height: w ? 640 : 0, top: 0, left: 0, right: w, bottom: 640, x: 0, y: 0, toJSON: () => ({}) }
    })

    const { rerender } = render(<Probe show={false} />)
    expect(FakeResizeObserver.instances).toHaveLength(0)

    rerender(<Probe show />)
    expect(size()).toBe('1110x640')
    const observer = FakeResizeObserver.instances[0]!
    expect(observer.observed).toEqual([screen.getByTestId('box')])

    width = 720
    act(() => observer.fire())
    expect(size()).toBe('720x640')

    rerender(<Probe show={false} />)
    expect(observer.disconnected).toBe(true)
  })
})
