import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { ProtectedTreeLeaf, type ProtectedLeafDenial } from '../ProtectedTreeLeaf'
import { expectNoAxeViolations } from '../../../test/axe'

const denial: ProtectedLeafDenial = {
  placeholderTitle: '(protected)',
  noSpaceAccess: false,
  marking: { label: 'UK OFFICIAL BANANA', level: 'OFFICIAL' },
  reasons: [{ gate: 'SELECTOR_GRANT', passed: false, category: 'FRUIT', value: 'BANANA' }],
}

function renderLeaf(d: ProtectedLeafDenial = denial) {
  return render(
    <ul>
      <ProtectedTreeLeaf denial={d} indent={2} />
    </ul>,
  )
}

/**
 * A withheld page's place in a tree (design.md §6.7 / §21.8): the server's
 * placeholder title, the marking label, and one disclosure for the reasons.
 * Not a link — there is nothing to open — and nothing focusable but that
 * one button.
 */
describe('ProtectedTreeLeaf', () => {
  it("renders the server's placeholder title and label verbatim, and is not a link", () => {
    renderLeaf()
    expect(screen.getByText('(protected)')).toBeInTheDocument()
    expect(screen.getByText('UK OFFICIAL BANANA')).toBeInTheDocument()
    expect(screen.queryByRole('link')).toBeNull()
    expect(screen.queryByRole('menu')).toBeNull()
  })

  it('has exactly one focusable control, the disclosure, whose state rides on aria-expanded', () => {
    renderLeaf()
    const buttons = screen.getAllByRole('button')
    expect(buttons).toHaveLength(1)
    const why = screen.getByRole('button', { name: 'Why is this page protected?' })
    expect(why).toHaveAttribute('aria-expanded', 'false')
    fireEvent.click(why)
    expect(why).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByText('Selector grant: BANANA is not granted to you in this space.')).toBeVisible()
    fireEvent.click(why)
    expect(why).toHaveAttribute('aria-expanded', 'false')
  })

  it('shows only the title and the space sentence when the caller holds no access to the space', () => {
    renderLeaf({
      placeholderTitle: '(protected)',
      noSpaceAccess: true,
      marking: null,
      reasons: [{ gate: 'SPACE_ACCESS', passed: false }],
    })
    expect(screen.getByText('(protected)')).toBeInTheDocument()
    expect(screen.getByText('You have no access to this space.')).toBeInTheDocument()
    expect(screen.queryByRole('button')).toBeNull()
    expect(screen.queryByText(/Protective marking/)).toBeNull()
  })

  it('has no axe violations, closed and open', async () => {
    renderLeaf()
    await expectNoAxeViolations()
    fireEvent.click(screen.getByRole('button', { name: 'Why is this page protected?' }))
    await expectNoAxeViolations()
  })
})
