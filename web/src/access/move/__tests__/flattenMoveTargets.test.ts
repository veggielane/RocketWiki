import { describe, expect, it } from 'vitest'
import { ancestorRestrictionsOf, flattenMoveTargets, type MoveTreeNode } from '../flattenMoveTargets'
import { serializeRuleNode } from '../../ruleSerializer'
import { group } from '../../ruleTypes'
import type { SpaceTreeForMoveQuery } from '../../../graphql/generated/graphql'

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

  it('accumulates a restricted node\'s own restrictions into its children\'s ancestor chains', () => {
    const tree: MoveTreeNode[] = [
      {
        id: 'restricted',
        title: 'Restricted Parent',
        ownViewRestrictions: [{ ruleId: 'rule-1', expressionJson: serializeRuleNode(group('export-cleared')) }],
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

  it('carries EVERY restriction of a multi-restriction node — ownViewRestrictions is a list, not a singular', () => {
    const tree: MoveTreeNode[] = [
      {
        id: 'doubly',
        title: 'Doubly Restricted',
        ownViewRestrictions: [
          { ruleId: 'r-eng', expressionJson: serializeRuleNode(group('engineering')) },
          { ruleId: 'r-export', expressionJson: serializeRuleNode(group('export-cleared')) },
        ],
        children: [{ id: 'child', title: 'Child' }],
      },
    ]
    const options = flattenMoveTargets(tree, 'excluded')
    expect(options.find((o) => o.id === 'doubly')!.ancestorRestrictions.map((r) => r.ruleId)).toEqual([
      'r-eng',
      'r-export',
    ])
    expect(options.find((o) => o.id === 'child')!.ancestorRestrictions.map((r) => r.ruleId)).toEqual([
      'r-eng',
      'r-export',
    ])
  })

  it('a node two levels deep accumulates restrictions from every ancestor, not just its immediate parent', () => {
    const tree: MoveTreeNode[] = [
      {
        id: 'top',
        title: 'Top',
        ownViewRestrictions: [{ ruleId: 'top-rule', expressionJson: serializeRuleNode(group('engineering')) }],
        children: [
          {
            id: 'mid',
            title: 'Mid',
            ownViewRestrictions: [{ ruleId: 'mid-rule', expressionJson: serializeRuleNode(group('export-cleared')) }],
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
        ownViewRestrictions: [{ ruleId: 'r1', expressionJson: serializeRuleNode(group('export-cleared')) }],
      },
      { id: 'open', title: 'Open' },
    ]
    const options = flattenMoveTargets(tree, 'excluded')
    expect(options.find((o) => o.id === 'open')!.ancestorRestrictions).toEqual([])
  })

  it('keeps a malformed stored expression as an unreadable (null-expression) restriction rather than dropping it', () => {
    const tree: MoveTreeNode[] = [
      {
        id: 'broken',
        title: 'Broken',
        ownViewRestrictions: [{ ruleId: 'r-bad', expressionJson: 'not json at all' }],
      },
    ]
    const options = flattenMoveTargets(tree, 'excluded')
    const restrictions = options.find((o) => o.id === 'broken')!.ancestorRestrictions
    expect(restrictions).toHaveLength(1)
    expect(restrictions[0]!.ruleId).toBe('r-bad')
    expect(restrictions[0]!.expression).toBeNull()
  })

  it('accepts the generated SpaceTreeForMove node shape without adaptation', () => {
    // Compile-time round-trip against the REAL generated type: if the
    // operation's shape drifts from MoveTreeNode, this stops compiling.
    const apiNode: SpaceTreeForMoveQuery['pageTree'][number] = {
      id: 'api-a',
      title: 'From API',
      sortOrder: 0,
      ownViewRestrictions: [{ ruleId: 'r1', expressionJson: serializeRuleNode(group('engineering')) }],
      children: [],
    }
    const options = flattenMoveTargets([apiNode], 'excluded')
    expect(options.find((o) => o.id === 'api-a')!.ancestorRestrictions.map((r) => r.ruleId)).toEqual(['r1'])
  })
})

describe('ancestorRestrictionsOf', () => {
  const tree: MoveTreeNode[] = [
    {
      id: 'top',
      title: 'Top',
      ownViewRestrictions: [{ ruleId: 'top-rule', expressionJson: serializeRuleNode(group('engineering')) }],
      children: [
        {
          id: 'mid',
          title: 'Mid',
          ownViewRestrictions: [{ ruleId: 'mid-rule', expressionJson: serializeRuleNode(group('export-cleared')) }],
          children: [{ id: 'leaf', title: 'Leaf', ownViewRestrictions: [{ ruleId: 'leaf-rule', expressionJson: serializeRuleNode(group('leaf-only')) }] }],
        },
      ],
    },
    { id: 'open', title: 'Open' },
  ]

  it('returns the accumulated ancestor chain, excluding the page\'s own restrictions', () => {
    // The page's own rules travel with it on a move and never change
    // (design.md §6.4) — only what it inherits from above is "current".
    expect(ancestorRestrictionsOf(tree, 'leaf').map((r) => r.ruleId)).toEqual(['top-rule', 'mid-rule'])
  })

  it('returns [] for a top-level page', () => {
    expect(ancestorRestrictionsOf(tree, 'top')).toEqual([])
    expect(ancestorRestrictionsOf(tree, 'open')).toEqual([])
  })

  it('returns [] for a page not present in the (depth-limited) tree', () => {
    expect(ancestorRestrictionsOf(tree, 'not-there')).toEqual([])
  })

  it('does not leak a sibling subtree\'s restrictions into the chain', () => {
    const forest: MoveTreeNode[] = [
      {
        id: 'restricted-branch',
        title: 'Restricted',
        ownViewRestrictions: [{ ruleId: 'r1', expressionJson: serializeRuleNode(group('export-cleared')) }],
        children: [{ id: 'r-child', title: 'R Child' }],
      },
      {
        id: 'open-branch',
        title: 'Open',
        children: [{ id: 'o-child', title: 'O Child' }],
      },
    ]
    expect(ancestorRestrictionsOf(forest, 'o-child')).toEqual([])
    expect(ancestorRestrictionsOf(forest, 'r-child').map((r) => r.ruleId)).toEqual(['r1'])
  })
})
