import { describe, expect, it } from 'vitest'
import {
  addChild,
  canRemoveNode,
  createAttrCondition,
  createCombinatorNode,
  createEveryoneCondition,
  createGroupCondition,
  createUserCondition,
  findNode,
  findParent,
  removeNode,
  ruleNodeToBuilderNode,
  toggleCombinator,
  updateNode,
  validateBuilderState,
  type BuilderNode,
} from '../builderState'
import { allOf, anyOf, attr, everyone, group, user } from '../ruleTypes'
import { parseRuleNode, serializeRuleNode } from '../ruleSerializer'

describe('validateBuilderState — the emission gate', () => {
  it('rejects a freshly created group condition (empty name)', () => {
    const result = validateBuilderState(createGroupCondition())
    expect(result.valid).toBe(false)
  })

  it('rejects a freshly created user condition (empty id)', () => {
    const result = validateBuilderState(createUserCondition())
    expect(result.valid).toBe(false)
  })

  it('rejects a freshly created attr condition (no attribute, no values)', () => {
    const result = validateBuilderState(createAttrCondition())
    if (result.valid) throw new Error('expected invalid')
    // Both problems are reported, not just the first — an admin filling in
    // the attribute shouldn't have to re-submit to discover the values are
    // also missing.
    expect(result.issues.length).toBeGreaterThanOrEqual(2)
  })

  it('accepts everyone unconditionally', () => {
    const result = validateBuilderState(createEveryoneCondition())
    expect(result).toEqual({ valid: true, node: everyone() })
  })

  it('accepts a filled-in group condition', () => {
    const node: BuilderNode = { ...createGroupCondition(), group: 'engineering' }
    const result = validateBuilderState(node)
    expect(result).toEqual({ valid: true, node: group('engineering') })
  })

  it('accepts a filled-in attr condition', () => {
    const node: BuilderNode = { ...createAttrCondition(), attribute: 'nationality', in: ['NZ', 'US'] }
    const result = validateBuilderState(node)
    expect(result).toEqual({ valid: true, node: attr('nationality', ['NZ', 'US']) })
  })

  it('rejects an attr condition with a blank value in the list', () => {
    const node: BuilderNode = { ...createAttrCondition(), attribute: 'nationality', in: ['NZ', '  '] }
    const result = validateBuilderState(node)
    expect(result.valid).toBe(false)
  })

  it('rejects an empty combinator group (cannot happen through normal editing, but must still be caught)', () => {
    const emptyGroup: BuilderNode = { id: 'g1', kind: 'allOf', children: [] }
    const result = validateBuilderState(emptyGroup)
    expect(result.valid).toBe(false)
  })

  it('reports a deeply nested invalid leaf without swallowing the error', () => {
    const tree: BuilderNode = {
      id: 'root',
      kind: 'allOf',
      children: [{ ...createGroupCondition(), group: 'engineering' }, createCombinatorNode('anyOf')],
    }
    const result = validateBuilderState(tree)
    expect(result.valid).toBe(false)
  })

  it('accepts the fully filled-in §6.3 worked example and matches the exact wire shape', () => {
    const groupCond = { ...createGroupCondition(), group: 'engineering' }
    const attrCond = { ...createAttrCondition(), attribute: 'nationality', in: ['NZ', 'US'] }
    const exportGroup = { ...createGroupCondition(), group: 'export-cleared' }
    const tree: BuilderNode = {
      id: 'root',
      kind: 'allOf',
      children: [groupCond, { id: 'inner', kind: 'anyOf', children: [attrCond, exportGroup] }],
    }

    const result = validateBuilderState(tree)
    if (!result.valid) throw new Error('expected valid')
    expect(serializeRuleNode(result.node)).toBe(
      '{"allOf":[{"group":"engineering"},{"anyOf":[{"attr":"nationality","in":["NZ","US"]},{"group":"export-cleared"}]}]}',
    )
  })
})

describe('tree navigation and edits', () => {
  function sampleTree(): BuilderNode {
    return {
      id: 'root',
      kind: 'allOf',
      children: [
        { id: 'a', kind: 'group', group: 'engineering' },
        { id: 'inner', kind: 'anyOf', children: [{ id: 'b', kind: 'group', group: 'export-cleared' }] },
      ],
    }
  }

  it('findNode locates a node anywhere in the tree', () => {
    const tree = sampleTree()
    expect(findNode(tree, 'b')).toEqual({ id: 'b', kind: 'group', group: 'export-cleared' })
    expect(findNode(tree, 'missing')).toBeNull()
  })

  it('findParent locates the immediate parent group', () => {
    const tree = sampleTree()
    const parent = findParent(tree, 'b')
    expect(parent?.id).toBe('inner')
  })

  it('canRemoveNode is false for the root', () => {
    const tree = sampleTree()
    expect(canRemoveNode(tree, 'root')).toBe(false)
  })

  it('canRemoveNode is false for the only child of a group', () => {
    const tree = sampleTree()
    expect(canRemoveNode(tree, 'b')).toBe(false)
  })

  it('canRemoveNode is true when siblings exist', () => {
    const tree = sampleTree()
    expect(canRemoveNode(tree, 'a')).toBe(true)
  })

  it('removeNode is immutable — the original tree is untouched', () => {
    const tree = sampleTree()
    const updated = removeNode(tree, 'a')
    expect(findNode(tree, 'a')).not.toBeNull()
    expect(findNode(updated, 'a')).toBeNull()
    expect(findNode(updated, 'b')).not.toBeNull()
  })

  it('addChild appends to the target group without touching siblings', () => {
    const tree = sampleTree()
    const newLeaf = createGroupCondition()
    const updated = addChild(tree, 'inner', newLeaf)
    const innerAfter = findNode(updated, 'inner')
    if (!innerAfter || innerAfter.kind !== 'anyOf') throw new Error('expected anyOf group')
    expect(innerAfter.children).toHaveLength(2)
    expect(findNode(tree, newLeaf.id)).toBeNull() // original untouched
  })

  it('updateNode edits one leaf without touching the rest of the tree', () => {
    const tree = sampleTree()
    const updated = updateNode(tree, 'a', (node) => (node.kind === 'group' ? { ...node, group: 'design' } : node))
    const updatedLeaf = findNode(updated, 'a')
    expect(updatedLeaf).toEqual({ id: 'a', kind: 'group', group: 'design' })
    expect(findNode(tree, 'a')).toEqual({ id: 'a', kind: 'group', group: 'engineering' }) // original untouched
  })

  it('toggleCombinator flips allOf <-> anyOf in place', () => {
    const tree = sampleTree()
    const toggled = toggleCombinator(tree, 'root')
    expect(toggled.kind).toBe('anyOf')
    const toggledBack = toggleCombinator(toggled, 'root')
    expect(toggledBack.kind).toBe('allOf')
  })
})

describe('JSON -> builder state -> JSON round trip', () => {
  it('round-trips the §6.3 worked example unchanged', () => {
    const original = allOf([group('engineering'), anyOf([attr('nationality', ['NZ', 'US']), group('export-cleared')])])
    const json = serializeRuleNode(original)

    const builderTree = ruleNodeToBuilderNode(parseRuleNode(json))
    const result = validateBuilderState(builderTree)
    if (!result.valid) throw new Error('expected valid after round trip')

    expect(serializeRuleNode(result.node)).toBe(json)
  })

  it('round-trips a simple everyone rule', () => {
    const json = serializeRuleNode(everyone())
    const builderTree = ruleNodeToBuilderNode(parseRuleNode(json))
    const result = validateBuilderState(builderTree)
    if (!result.valid) throw new Error('expected valid after round trip')
    expect(serializeRuleNode(result.node)).toBe(json)
  })

  it('round-trips a user condition', () => {
    const json = serializeRuleNode(user('sub-123'))
    const builderTree = ruleNodeToBuilderNode(parseRuleNode(json))
    const result = validateBuilderState(builderTree)
    if (!result.valid) throw new Error('expected valid after round trip')
    expect(serializeRuleNode(result.node)).toBe(json)
  })
})
