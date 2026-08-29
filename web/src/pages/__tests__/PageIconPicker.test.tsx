import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { PageIconPicker } from '../PageIconPicker'
import type { PageIcon } from '../../graphql/generated/graphql'
import { expectNoAxeViolations } from '../../test/axe'

function renderPicker(value: PageIcon | null = null) {
  const onChange = vi.fn()
  render(<PageIconPicker value={value} onChange={onChange} />)
  return { onChange, field: () => screen.getByRole('combobox', { name: 'Icon' }) }
}

describe('PageIconPicker', () => {
  it('is a labelled control, so it is reachable by name and not just by sight', () => {
    renderPicker()
    expect(screen.getByRole('combobox', { name: 'Icon' })).toBeInTheDocument()
  })

  it('shows "No icon" rather than an empty field when a page has none', () => {
    renderPicker()
    expect(screen.getByRole('combobox', { name: 'Icon' })).toHaveTextContent('No icon')
  })

  it('names every option in text, so the list is readable and type-ahead works', () => {
    const { field } = renderPicker()
    fireEvent.mouseDown(field())
    expect(screen.getByRole('option', { name: 'Rocket' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Checklist' })).toBeInTheDocument()
  })

  it('reports the chosen icon by its wire name', () => {
    const { onChange, field } = renderPicker()
    fireEvent.mouseDown(field())
    fireEvent.click(screen.getByRole('option', { name: 'Rocket' }))
    expect(onChange).toHaveBeenCalledWith('ROCKET')
  })

  it('offers a way back to no icon at all', () => {
    // Setting one must not be a one-way door: null is a legal value for the
    // field everywhere on the wire, so clearing has to be something a user can
    // actually do rather than an absence of selection they cannot reach.
    const { onChange, field } = renderPicker('ROCKET')
    expect(field()).toHaveTextContent('Rocket')
    fireEvent.mouseDown(field())
    fireEvent.click(screen.getByRole('option', { name: 'No icon' }))
    expect(onChange).toHaveBeenCalledWith(null)
  })

  it('opens from the keyboard', () => {
    const { field } = renderPicker()
    field().focus()
    fireEvent.keyDown(field(), { key: 'Enter' })
    expect(screen.getByRole('option', { name: 'No icon' })).toBeInTheDocument()
  })

  it('keeps an icon this build does not know selected instead of silently clearing it', () => {
    // A page synced from an instance with a newer icon set (design.md §12).
    // Dropping the option would show "No icon", and the next save — which
    // always sends the field — would write that guess back over the page.
    const { onChange, field } = renderPicker('SATELLITE' as PageIcon)
    expect(field()).toHaveTextContent('SATELLITE')
    expect(field()).not.toHaveTextContent('No icon')
    fireEvent.mouseDown(field())
    expect(screen.getByRole('option', { name: 'SATELLITE' })).toHaveAttribute('aria-selected', 'true')
    // Nothing was reported: the page's icon is untouched until someone
    // deliberately changes it, which is the whole point.
    expect(onChange).not.toHaveBeenCalled()
  })

  it('has no axe violations closed or open', async () => {
    const { field } = renderPicker('ROCKET')
    await expectNoAxeViolations()
    fireEvent.mouseDown(field())
    await screen.findByRole('option', { name: 'Rocket' })
    await expectNoAxeViolations()
  })
})
