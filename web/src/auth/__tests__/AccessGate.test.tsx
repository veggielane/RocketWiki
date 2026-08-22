import type { ReactElement } from 'react'
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { AccessGate } from '../AccessGate'

// NotFoundPage links back to "/" via react-router's Link, which needs a
// router context even though nothing here navigates.
function renderGate(ui: ReactElement) {
  return render(<MemoryRouter>{ui}</MemoryRouter>)
}

/**
 * `AccessGate` is the shared logic behind every "absent rather than
 * forbidden" surface (design.md §6.7) — RequireInstanceAdmin, the
 * permissions route, and the space-grants route all render through it.
 * One bug here is a bug in all four places at once, so its branching is
 * pinned down directly rather than only indirectly via each caller.
 */
describe('AccessGate', () => {
  it('shows a loading state and not the content while loading', () => {
    renderGate(
      <AccessGate allowed={undefined} loading>
        <div>secret content</div>
      </AccessGate>,
    )
    expect(screen.queryByText('secret content')).not.toBeInTheDocument()
  })

  it('renders children once allowed is true and loading has finished', () => {
    renderGate(
      <AccessGate allowed={true} loading={false}>
        <div>secret content</div>
      </AccessGate>,
    )
    expect(screen.getByText('secret content')).toBeInTheDocument()
  })

  it('renders the not-found page, not the content, when allowed is false', () => {
    renderGate(
      <AccessGate allowed={false} loading={false}>
        <div>secret content</div>
      </AccessGate>,
    )
    expect(screen.queryByText('secret content')).not.toBeInTheDocument()
    expect(screen.getByText('404')).toBeInTheDocument()
  })

  it('fails closed — renders not-found when allowed is undefined and loading has finished (e.g. the query errored)', () => {
    renderGate(
      <AccessGate allowed={undefined} loading={false}>
        <div>secret content</div>
      </AccessGate>,
    )
    expect(screen.queryByText('secret content')).not.toBeInTheDocument()
    expect(screen.getByText('404')).toBeInTheDocument()
  })

  it('the denied and not-found states are visually identical (design.md §6.7)', () => {
    const denied = renderGate(
      <AccessGate allowed={false} loading={false}>
        <div>secret content</div>
      </AccessGate>,
    )
    const deniedHtml = denied.container.innerHTML
    denied.unmount()

    const notFound = renderGate(
      <AccessGate allowed={undefined} loading={false}>
        {null}
      </AccessGate>,
    )
    expect(deniedHtml).toBe(notFound.container.innerHTML)
  })
})
