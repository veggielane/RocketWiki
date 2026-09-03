import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MarkingLabelChip } from '../MarkingLabelChip'
import { expectNoAxeViolations } from '../../test/axe'

describe('MarkingLabelChip', () => {
  it("renders the server's whole label verbatim, with a lead-in only a screen reader hears", () => {
    render(<MarkingLabelChip label="UK SECRET APPLE NORTH AUS/NZ EYES ONLY" level="SECRET" />)
    const chip = screen.getByText('UK SECRET APPLE NORTH AUS/NZ EYES ONLY')
    // The lead-in is part of the accessible text, and the label follows it
    // byte-for-byte — no case change, no re-ordering, no bracketing.
    expect(chip.closest('span')?.textContent).toBe('Protective marking: UK SECRET APPLE NORTH AUS/NZ EYES ONLY')
  })

  it('composes nothing — a bare level label is rendered as given', () => {
    render(<MarkingLabelChip label="TOP SECRET" level="TOP_SECRET" />)
    expect(screen.getByText('TOP SECRET')).toBeInTheDocument()
    expect(screen.queryByText(/UK TOP SECRET/)).toBeNull()
  })

  it('has no axe violations', async () => {
    render(<MarkingLabelChip label="UK OFFICIAL" level="OFFICIAL" />)
    await expectNoAxeViolations()
  })
})
