import { describe, expect, it } from 'vitest'
import { flattenMoveTargets, type MoveTreeNode } from '../flattenMoveTargets'
import { serializeRuleNode } from '../../ruleSerializer'
import { group } from '../../ruleTypes'

describe('flattenMoveTargets', () => {
  it('always includes the space root as an unrestricted option', () => {
    const options = flattenMoveTargets([], 'page-x')
    expect(options).toEqual([{ id: null, title: '(space root)', ancestorRestrictions: [] }])
  })

  it('flattens a simple tree with no restrictions', () => {
    const tree: MoveTreeNode[] = [
      { id: 'a', title: 'A', children: [{ id: 'b', title: 'B' }] },
    ]
    const options = flattenMoveTargets(tree, 'excluded')
    expect(options.map((o) => o.id)).toEqual([null, 'a', 'b'])
    expect(options.every((o) => o.ancestorRestrictions.length === 0)).toBe(true)
  })

  it('excludes the moved page and its entire subtree', () => {
    const tree: MoveTreeNode[] = [
      {
        id: 'a',
        title: 'A',
        children: [{ id: 'b', title: 'B', children: [{ id: 'c', title: 'C' }] }],
      },
    ]
    const options = flattenMoveTargets(tree, 'b')
    expect(options.map((o) => o.id)).toEqual([null, 'a']) // b and its child c are excluded
  })

  it('accumulates a restricted node\'s own restriction into its children\'s ancestor chains', () => {
    const tree: MoveTreeNode[] = [
      {
        id: 'restricted',
        title: 'Restricted Parent',
        ownViewRestriction: { ruleId: 'rule-1', expressionJson: serializeRuleNode(group('export-cleared')) },
        children: [{ id: 'child', title: 'Child' }],
      },
    ]
    const options = flattenMoveTargets(tree, 'excluded')

    const restrictedOption = options.find((o) => o.id === 'restricted')!
    expect(restrictedOption.ancestorRestrictions).toEqual([
      { ruleId: 'rule-1', pageId: 'restricted', pageTitle: 'Restricted Parent', action: 'view', expression: group('export-cleared') },
    ])

    // The child inherits the parent's restriction plus its own (none here) —
    // moving a page under "Restricted Parent" or under "Child" both mean
    // inheriting the same restriction, since it originates above both.
    const childOption = options.find((o) => o.id === 'child')!
    expect(childOption.ancestorRestrictions).toEqual(restrictedOption.ancestorRestrictions)
  })

  it('a node two levels deep accumulates restrictions from every ancestor, not just its immediate parent', () => {
    const tree: MoveTreeNode[] = [
      {
        id: 'top',
        title: 'Top',
        ownViewRestriction: { ruleId: 'top-rule', expressionJson: serializeRuleNode(group('engineering')) },
        children: [
          {
            id: 'mid',
            title: 'Mid',
            ownViewRestriction: { ruleId: 'mid-rule', expressionJson: serializeRuleNode(group('export-cleared')) },
            children: [{ id: 'leaf', title: 'Leaf' }],
          },
        ],
      },
    ]
    const options = flattenMoveTargets(tree, 'excluded')
    const leafOption = options.find((o) => o.id === 'leaf')!
    expect(leafOption.ancestorRestrictions.map((r) => r.ruleId)).toEqual(['top-rule', 'mid-rule'])
  })

  it('does not let unrestricted siblings of a restricted subtree pick up its restrictions', () => {
    const tree: MoveTreeNode[] = [
      {
        id: 'restricted',
        title: 'Restricted',
        ownViewRestriction: { ruleId: 'r1', expressionJson: serializeRuleNode(group('export-cleared')) },
      },
      { id: 'open', title: 'Open' },
    ]
    const options = flattenMoveTargets(tree, 'excluded')
    expect(options.find((o) => o.id === 'open')!.ancestorRestrictions).toEqual([])
  })
})
