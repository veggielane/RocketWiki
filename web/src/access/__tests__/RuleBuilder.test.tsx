import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { RuleBuilder } from '../RuleBuilder'
import { allOf, group, user } from '../ruleTypes'
import { serializeRuleNode } from '../ruleSerializer'
import type { ValidationResult } from '../builderState'

/**
 * Component-level tests over the rule builder's actual DOM, complementing
 * the pure-logic tests in builderState.test.ts / ruleSerializer.test.ts.
 * These check that user interaction correctly drives the emission gate —
 * that an incomplete edit reports `valid: false` rather than silently
 * emitting something malformed, and that a complete edit reports the
 * exact expected wire shape.
 */
describe('RuleBuilder', () => {
  it('reports the initial value as valid on mount', () => {
    const onChange = vi.fn<(result: ValidationResult) => void>()
    render(<RuleBuilder initialValue={user('sub-1')} onChange={onChange} groups={[]} attributes={[]} />)

    expect(onChange).toHaveBeenCalledTimes(1)
    const result = onChange.mock.calls[0]![0]
    expect(result.valid).toBe(true)
    if (result.valid) expect(result.node).toEqual(user('sub-1'))
  })

  it('reports invalid while a required field is blank, and valid again once filled', () => {
    const onChange = vi.fn<(result: ValidationResult) => void>()
    render(<RuleBuilder initialValue={user('sub-1')} onChange={onChange} groups={[]} attributes={[]} />)

    const input = screen.getByLabelText('User ID') as HTMLInputElement
    fireEvent.change(input, { target: { value: '' } })
    expect(onChange.mock.calls.at(-1)![0].valid).toBe(false)

    fireEvent.change(input, { target: { value: 'sub-42' } })
    const last = onChange.mock.calls.at(-1)![0]
    expect(last.valid).toBe(true)
    if (last.valid) expect(last.node).toEqual(user('sub-42'))
  })

  it('disables removing the only child of a group, and enables it once a sibling is added', async () => {
    const onChange = vi.fn<(result: ValidationResult) => void>()
    render(
      <RuleBuilder initialValue={allOf([group('engineering')])} onChange={onChange} groups={[]} attributes={[]} />,
    )

    const removeButtons = () => screen.getAllByLabelText('Remove condition')
    expect(removeButtons()).toHaveLength(1)
    expect(removeButtons()[0]).toBeDisabled()

    fireEvent.click(screen.getByRole('button', { name: /add condition/i }))
    fireEvent.click(await screen.findByRole('menuitem', { name: 'User' }))

    expect(removeButtons()).toHaveLength(2)
    for (const btn of removeButtons()) {
      expect(btn).not.toBeDisabled()
    }

    // The new condition is blank, so the overall rule is invalid until filled.
    expect(onChange.mock.calls.at(-1)![0].valid).toBe(false)

    fireEvent.change(screen.getByLabelText('User ID'), { target: { value: 'sub-1' } })

    const last = onChange.mock.calls.at(-1)![0]
    expect(last.valid).toBe(true)
    if (last.valid) {
      expect(serializeRuleNode(last.node)).toBe(
        '{"allOf":[{"group":"engineering"},{"user":"sub-1"}]}',
      )
    }
  })
})
