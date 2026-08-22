import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { MovePageDialog, type MoveTargetOption } from '../MovePageDialog'
import { group } from '../../access/ruleTypes'
import type { RestrictionSummary } from '../../access/move/visibilityChange'

const restrictedRule: RestrictionSummary = {
  ruleId: 'export-control',
  pageId: 'restricted-parent',
  pageTitle: 'Export-Controlled Docs',
  action: 'view',
  expression: group('export-cleared'),
}

const targets: MoveTargetOption[] = [
  { id: 'open-parent', title: 'Open Parent', ancestorRestrictions: [] },
  { id: 'restricted-parent', title: 'Export-Controlled Docs', ancestorRestrictions: [restrictedRule] },
]

describe('MovePageDialog', () => {
  it('disables Move until a target is selected', () => {
    render(
      <MovePageDialog
        open
        onClose={() => {}}
        pageTitle="Onboarding"
        currentAncestorRestrictions={[]}
        targetOptions={targets}
        onConfirm={() => {}}
      />,
    )
    expect(screen.getByRole('button', { name: /move/i })).toBeDisabled()
  })

  it('shows no warning when moving between equally unrestricted positions', () => {
    render(
      <MovePageDialog
        open
        onClose={() => {}}
        pageTitle="Onboarding"
        currentAncestorRestrictions={[]}
        targetOptions={targets}
        onConfirm={() => {}}
      />,
    )

    const input = screen.getByLabelText('New parent')
    fireEvent.mouseDown(input)
    fireEvent.change(input, { target: { value: 'Open Parent' } })
    fireEvent.click(screen.getByText('Open Parent'))

    expect(screen.getByText('No change to who can see this page.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Move' })).toBeEnabled()
  })

  it('warns and relabels the button when moving under a newly-restricted parent', () => {
    render(
      <MovePageDialog
        open
        onClose={() => {}}
        pageTitle="Onboarding"
        currentAncestorRestrictions={[]}
        targetOptions={targets}
        onConfirm={() => {}}
      />,
    )

    const input = screen.getByLabelText('New parent')
    fireEvent.mouseDown(input)
    fireEvent.change(input, { target: { value: 'Export-Controlled' } })
    fireEvent.click(screen.getByText('Export-Controlled Docs'))

    expect(screen.getByText('This move changes who can see this page.')).toBeInTheDocument()
    expect(screen.getByText('group: export-cleared')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Move anyway' })).toBeEnabled()
  })

  it('calls onConfirm with the selected target id', () => {
    const onConfirm = vi.fn()
    render(
      <MovePageDialog
        open
        onClose={() => {}}
        pageTitle="Onboarding"
        currentAncestorRestrictions={[]}
        targetOptions={targets}
        onConfirm={onConfirm}
      />,
    )

    const input = screen.getByLabelText('New parent')
    fireEvent.mouseDown(input)
    fireEvent.change(input, { target: { value: 'Open Parent' } })
    fireEvent.click(screen.getByText('Open Parent'))
    fireEvent.click(screen.getByRole('button', { name: 'Move' }))

    expect(onConfirm).toHaveBeenCalledWith('open-parent')
  })

  it('warns when moving away from a restricted parent removes a restriction', () => {
    render(
      <MovePageDialog
        open
        onClose={() => {}}
        pageTitle="Onboarding"
        currentAncestorRestrictions={[restrictedRule]}
        targetOptions={targets}
        onConfirm={() => {}}
      />,
    )

    const input = screen.getByLabelText('New parent')
    fireEvent.mouseDown(input)
    fireEvent.change(input, { target: { value: 'Open Parent' } })
    fireEvent.click(screen.getByText('Open Parent'))

    expect(screen.getByText('This move changes who can see this page.')).toBeInTheDocument()
    expect(screen.getByText(/currently apply would no longer/i)).toBeInTheDocument()
  })
})
