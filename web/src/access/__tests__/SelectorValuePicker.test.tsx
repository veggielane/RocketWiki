import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { SelectorValuePicker } from '../SelectorValuePicker'
import { expectNoAxeViolations } from '../../test/axe'

const categories = [
  { name: 'FRUIT', description: 'Fruit programme compartments', values: ['APPLE', 'BANANA'] },
  { name: 'REGION', description: null, values: ['NORTH', 'SOUTH'] },
]

async function openAndType(text: string) {
  const input = screen.getByLabelText('Selector values')
  fireEvent.mouseDown(input)
  fireEvent.change(input, { target: { value: text } })
  return input
}

/**
 * The values an access grant confers (design.md §21.15 / §6.4). Unlike a
 * page's marking, a grant may carry several values in one category — a
 * grant that confers both APPLE and BANANA is the ordinary way to give
 * someone both.
 */
describe('SelectorValuePicker', () => {
  it('offers every configured value, grouped by category, and nothing typed by hand', async () => {
    render(<SelectorValuePicker categories={categories} value={[]} onChange={vi.fn()} />)
    await openAndType('')
    for (const value of ['APPLE', 'BANANA', 'NORTH', 'SOUTH']) {
      expect(await screen.findByRole('option', { name: value })).toBeInTheDocument()
    }
    expect(screen.getByText('FRUIT')).toBeInTheDocument()
    expect(screen.getByText('REGION')).toBeInTheDocument()
  })

  it('allows several values in one category', async () => {
    const onChange = vi.fn()
    render(
      <SelectorValuePicker categories={categories} value={[{ category: 'FRUIT', value: 'APPLE' }]} onChange={onChange} />,
    )
    await openAndType('BAN')
    fireEvent.click(await screen.findByRole('option', { name: 'BANANA' }))
    expect(onChange).toHaveBeenCalledWith([
      { category: 'FRUIT', value: 'APPLE' },
      { category: 'FRUIT', value: 'BANANA' },
    ])
  })

  it('labels each chip with its category as well as its value', () => {
    render(
      <SelectorValuePicker categories={categories} value={[{ category: 'REGION', value: 'NORTH' }]} onChange={vi.fn()} />,
    )
    expect(screen.getByText('REGION: NORTH')).toBeInTheDocument()
  })

  it('keeps a value the grant carries that no category offers any more, so it can still be removed', () => {
    render(
      <SelectorValuePicker categories={categories} value={[{ category: 'LEGACY', value: 'OLD' }]} onChange={vi.fn()} />,
    )
    expect(screen.getByText('LEGACY: OLD')).toBeInTheDocument()
  })

  it('is disabled and says why when the instance configures no categories', () => {
    render(<SelectorValuePicker categories={[]} value={[]} onChange={vi.fn()} />)
    expect(screen.getByLabelText('Selector values')).toBeDisabled()
    expect(screen.getByText(/No selector categories are configured/)).toBeInTheDocument()
  })

  it('has no axe violations', async () => {
    render(
      <SelectorValuePicker categories={categories} value={[{ category: 'FRUIT', value: 'APPLE' }]} onChange={vi.fn()} />,
    )
    await expectNoAxeViolations()
  })
})
