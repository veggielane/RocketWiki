import { describe, expect, it } from 'vitest'
import { computeVisibilityChange, type RestrictionSummary } from '../visibilityChange'
import { attr, group } from '../../ruleTypes'

function restriction(overrides: Partial<RestrictionSummary> = {}): RestrictionSummary {
  return {
    ruleId: 'r1',
    pageId: 'p1',
    pageTitle: 'Some Page',
    action: 'view',
    expression: group('engineering'),
    ...overrides,
  }
}

describe('computeVisibilityChange', () => {
  it('reports no change when moving under an identically-restricted ancestor chain', () => {
    const shared = [restriction({ ruleId: 'r1' })]
    const result = computeVisibilityChange(shared, shared)
    expect(result).toEqual({ changed: false, added: [], removed: [] })
  })

  it('reports no change between two unrestricted positions', () => {
    const result = computeVisibilityChange([], [])
    expect(result).toEqual({ changed: false, added: [], removed: [] })
  })

  it('detects a newly added restriction when moving under a restricted parent', () => {
    const newRule = restriction({ ruleId: 'export-control', expression: attr('nationality', ['NZ', 'US']) })
    const result = computeVisibilityChange([], [newRule])
    expect(result.changed).toBe(true)
    expect(result.added).toEqual([newRule])
    expect(result.removed).toEqual([])
  })

  it('detects a removed restriction when moving out from under a restricted parent', () => {
    const oldRule = restriction({ ruleId: 'export-control' })
    const result = computeVisibilityChange([oldRule], [])
    expect(result.changed).toBe(true)
    expect(result.added).toEqual([])
    expect(result.removed).toEqual([oldRule])
  })

  it('detects a swap — one restriction removed, a different one added', () => {
    const oldRule = restriction({ ruleId: 'old-team-only', pageTitle: 'Old Parent' })
    const newRule = restriction({ ruleId: 'new-team-only', pageTitle: 'New Parent' })
    const result = computeVisibilityChange([oldRule], [newRule])
    expect(result.changed).toBe(true)
    expect(result.added).toEqual([newRule])
    expect(result.removed).toEqual([oldRule])
  })

  it('is unaffected by the page being moved having its own restrictions (only ancestor restrictions are compared)', () => {
    // The page's own restriction is deliberately not part of either list —
    // callers pass ancestor restrictions only, per design.md §6.4: the
    // page's own rules travel with it and never change on a move.
    const sharedAncestor = [restriction({ ruleId: 'space-wide' })]
    const result = computeVisibilityChange(sharedAncestor, sharedAncestor)
    expect(result.changed).toBe(false)
  })

  it('does not treat rules with the same expression but different ids as identical', () => {
    // Two restrictions can have the same rule expression by coincidence;
    // identity is by rule id, not by expression content.
    const a = restriction({ ruleId: 'a' })
    const b = restriction({ ruleId: 'b' })
    const result = computeVisibilityChange([a], [b])
    expect(result.changed).toBe(true)
    expect(result.added.map((r) => r.ruleId)).toEqual(['b'])
    expect(result.removed.map((r) => r.ruleId)).toEqual(['a'])
  })
})
