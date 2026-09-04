import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { RuleBuilder } from '../RuleBuilder'
import { allOf, attr, group, user } from '../ruleTypes'
import { serializeRuleNode } from '../ruleSerializer'
import { expectNoAxeViolations } from '../../test/axe'
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

  it('has no axe violations with a nested AND/OR group rendered', async () => {
    render(
      <RuleBuilder
        initialValue={allOf([group('engineering'), user('sub-1')])}
        onChange={() => {}}
        groups={['engineering']}
        attributes={[]}
      />,
    )
    screen.getByLabelText('User ID')
    await expectNoAxeViolations()
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

/**
 * Where a validation message ends up.
 *
 * The builder used to route issues by testing the English message text —
 * `m.includes('value')` — and `'Values must not be empty.'` starts with a
 * capital V, so it matched nothing: the rule refused to save, no field turned
 * red, and the sentence appeared nowhere on screen. `'Attribute is required.'`
 * had the opposite problem, colouring a Select that had no helper line to print
 * it in. Both are routed by a `field` tag now.
 */
describe('rule validation messages reach the control they are about', () => {
  const ATTRIBUTES = [{ key: 'nationality', displayName: 'Nationality', allowedValues: ['UK', 'US'] }]

  it('writes down "at least one value is required" under the In field', () => {
    render(
      <RuleBuilder initialValue={attr('nationality', [])} onChange={() => {}} groups={[]} attributes={ATTRIBUTES} />,
    )

    const field = screen.getByLabelText('In')
    const helper = document.getElementById(field.getAttribute('aria-describedby')?.split(' ')[0] ?? '')
    expect(helper?.textContent).toContain('At least one value is required.')
  })

  it('writes down "Values must not be empty." — the message that used to match no branch', () => {
    render(
      <RuleBuilder initialValue={attr('nationality', ['  '])} onChange={() => {}} groups={[]} attributes={ATTRIBUTES} />,
    )

    expect(screen.getByText('Values must not be empty.')).toBeInTheDocument()
  })

  it('writes down "Attribute is required." rather than only colouring the Select', () => {
    render(<RuleBuilder initialValue={attr('', [])} onChange={() => {}} groups={[]} attributes={ATTRIBUTES} />)

    expect(screen.getByText('Attribute is required.')).toBeInTheDocument()
  })

  it('keeps one field’s problem out of another field’s helper line', () => {
    // Every issue on the node used to be joined into every control, so a blank
    // attribute also claimed the In field was wrong.
    render(<RuleBuilder initialValue={attr('', [])} onChange={() => {}} groups={[]} attributes={ATTRIBUTES} />)

    const field = screen.getByLabelText('In')
    const helper = document.getElementById(field.getAttribute('aria-describedby')?.split(' ')[0] ?? '')
    expect(helper?.textContent).not.toContain('Attribute is required.')
  })
})
