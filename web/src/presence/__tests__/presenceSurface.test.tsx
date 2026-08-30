import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { PresenceSurface } from '../PresenceSurface'
import type { PointerPosition } from '../../realtime/types'

/**
 * The capture box and the overlay, as one thing.
 *
 * They have to be the same box — positions are fractions of the capturing
 * element's rect, so an overlay measured against anything else draws every
 * remote cursor in the wrong place. Both pages used to pair them by hand.
 */

const pointer = (over: Partial<PointerPosition> = {}): PointerPosition => ({
  userId: 'u1',
  displayName: 'Ada',
  colour: '#2e7d32',
  x: 0.5,
  y: 0.5,
  ...over,
})

/**
 * jsdom lays nothing out, so every rect is 0×0 and the collapsed-box guard
 * would swallow every sample. Staging a rect is what makes the coordinate
 * arithmetic testable at all.
 */
function surfaceWithRect(rect: { left: number; top: number; width: number; height: number }) {
  const surface = screen.getByTestId('surface')
  surface.getBoundingClientRect = () =>
    ({ ...rect, right: rect.left + rect.width, bottom: rect.top + rect.height, x: rect.left, y: rect.top }) as DOMRect
  return surface
}

function renderSurface(pointers: Map<string, PointerPosition> = new Map()) {
  const recordPointer = vi.fn()
  render(
    <PresenceSurface pointers={pointers} recordPointer={recordPointer} sx={{ p: 1 }}>
      <div data-testid="content">page content</div>
    </PresenceSurface>,
  )
  // The surface is the element the content sits in.
  const surface = screen.getByTestId('content').parentElement!
  surface.setAttribute('data-testid', 'surface')
  return { recordPointer }
}

describe('PresenceSurface — what gets broadcast', () => {
  it('reports a pointer as fractions of its own rect', () => {
    const { recordPointer } = renderSurface()
    const surface = surfaceWithRect({ left: 100, top: 50, width: 400, height: 200 })

    fireEvent.mouseMove(surface, { clientX: 300, clientY: 150 })

    expect(recordPointer).toHaveBeenCalledWith(0.5, 0.5)
  })

  it('measures from the surface, not from whichever child the pointer is over', () => {
    // `currentTarget`, never `target` — a fraction of a paragraph's box would
    // put the cursor somewhere else entirely on the other screen.
    const { recordPointer } = renderSurface()
    surfaceWithRect({ left: 0, top: 0, width: 200, height: 100 })

    fireEvent.mouseMove(screen.getByTestId('content'), { clientX: 50, clientY: 25 })

    expect(recordPointer).toHaveBeenCalledWith(0.25, 0.25)
  })

  it('sends nothing from a collapsed box rather than broadcasting Infinity', () => {
    const { recordPointer } = renderSurface()
    const surface = surfaceWithRect({ left: 0, top: 0, width: 0, height: 0 })

    fireEvent.mouseMove(surface, { clientX: 10, clientY: 10 })

    expect(recordPointer).not.toHaveBeenCalled()
  })
})

describe('PresenceSurface — the overlay it carries', () => {
  it('draws a remote cursor at the fraction it was sent', () => {
    renderSurface(new Map([['u1', pointer({ x: 0.25, y: 0.75 })]]))

    const cursor = screen.getByText('Ada').parentElement!
    expect(cursor).toHaveStyle({ left: '25%', top: '75%' })
  })

  it('keeps the overlay out of the accessibility tree', () => {
    // Other people's names are decoration on your page, not content: they were
    // live text appearing and disappearing in the reading order as a mouse moved.
    const { container } = render(
      <PresenceSurface pointers={new Map([['u1', pointer()]])} recordPointer={vi.fn()}>
        <div>content</div>
      </PresenceSurface>,
    )

    const overlay = container.querySelector('[aria-hidden="true"]')
    expect(overlay).not.toBeNull()
    expect(overlay).toContainElement(screen.getByText('Ada'))
  })

  it('never intercepts the real reader’s own mouse', () => {
    const { container } = render(
      <PresenceSurface pointers={new Map([['u1', pointer()]])} recordPointer={vi.fn()}>
        <div>content</div>
      </PresenceSurface>,
    )

    expect(container.querySelector('[aria-hidden="true"]')).toHaveStyle({ pointerEvents: 'none' })
  })

  it('renders no overlay element at all when nobody is pointing', () => {
    // An empty full-bleed positioned layer stops axe computing the background
    // of everything underneath it, turning the page's contrast checks into
    // abstentions rather than passes.
    const { container } = render(
      <PresenceSurface pointers={new Map()} recordPointer={vi.fn()}>
        <div>content</div>
      </PresenceSurface>,
    )

    expect(container.querySelector('[aria-hidden="true"]')).toBeNull()
  })
})

describe('PresenceSurface — the motion the overlay is allowed', () => {
  /** Every rule emotion has emitted for this render. */
  const emittedCss = () =>
    Array.from(document.querySelectorAll('style'))
      .map((style) => style.textContent ?? '')
      .join('')

  it('glides between samples, so a cursor does not teleport 20 times a second', () => {
    render(
      <PresenceSurface pointers={new Map([['u1', pointer()]])} recordPointer={vi.fn()}>
        <div>content</div>
      </PresenceSurface>,
    )

    expect(emittedCss()).toMatch(/transition:\s*left 80ms linear,\s*top 80ms linear/)
  })

  it('drops the glide for a reader who asked the OS for less motion', () => {
    // Other people's cursors sliding over text being read is exactly the
    // involuntary movement the preference exists to stop, and there is no
    // setting anywhere to turn presence cursors off.
    render(
      <PresenceSurface pointers={new Map([['u1', pointer()]])} recordPointer={vi.fn()}>
        <div>content</div>
      </PresenceSurface>,
    )

    expect(emittedCss()).toMatch(/@media \(prefers-reduced-motion: reduce\)\{[^}]*transition:\s*none/)
  })
})
