import { describe, expect, it } from 'vitest'
import type { SpacePageTreeQuery, SpaceTreeForMoveQuery } from '../../graphql/generated/graphql'
import { isProtectedEntry, readableNodes, readableTree } from '../treeEntries'
import { flattenParentOptions } from '../parentOptions'
import { filterTreeByLabel } from '../../labels/filterTreeByLabel'
import { flattenMoveTargets } from '../../access/move/flattenMoveTargets'

const marking = { level: 'OFFICIAL' as const, levelName: 'OFFICIAL', eyesOnly: [], ukPrefix: true, label: 'UK OFFICIAL', selectors: [] }
const denial = { placeholderTitle: '(protected)', noSpaceAccess: false, marking, reasons: [] }

const protectedLeaf = { __typename: 'ProtectedTreeNode' as const, title: '(protected)', sortOrder: 1, denial }

/**
 * The tree's union (design.md §6.7 / §21.8), for every consumer that wants
 * readable pages only. Typed against the REAL generated shapes so a change
 * to either tree document that stops satisfying the consumers' node types
 * fails here at compile time.
 */
describe('isProtectedEntry', () => {
  it('branches on the discriminator and nothing else', () => {
    const readable = { __typename: 'PageTreeNode' as const, id: 'p' }
    const undiscriminated: { __typename?: string; id: string } = { id: 'p' }
    expect(isProtectedEntry(protectedLeaf)).toBe(true)
    expect(isProtectedEntry(readable)).toBe(false)
    // A fixture built before the union existed carries no discriminator and
    // is readable — a missing field is never read as a placeholder.
    expect(isProtectedEntry(undiscriminated)).toBe(false)
  })
})

describe('readableNodes', () => {
  it('drops placeholders at one level and leaves children as they came', () => {
    const entries = [
      { __typename: 'PageTreeNode' as const, id: 'a', children: [protectedLeaf] },
      protectedLeaf,
      { __typename: 'PageTreeNode' as const, id: 'b', children: [] },
    ]
    const readable = readableNodes(entries)
    expect(readable.map((n) => n.id)).toEqual(['a', 'b'])
    expect(readable[0]!.children).toHaveLength(1)
  })
})

describe('readableTree', () => {
  it('drops placeholders at every depth, keeping readable pages in order', () => {
    interface Node {
      __typename: 'PageTreeNode'
      id: string
      children?: Entry[]
    }
    type Entry = Node | typeof protectedLeaf
    const entries: Entry[] = [
      protectedLeaf,
      {
        __typename: 'PageTreeNode',
        id: 'a',
        children: [protectedLeaf, { __typename: 'PageTreeNode', id: 'a1', children: [protectedLeaf] }],
      },
      { __typename: 'PageTreeNode', id: 'b' },
    ]
    const readable = readableTree(entries)
    expect(readable.map((n) => n.id)).toEqual(['a', 'b'])
    expect(readable[0]!.children?.map((n) => n.id)).toEqual(['a1'])
    expect(readable[0]!.children?.[0]?.children).toEqual([])
  })

  it('feeds the parent picker and the label filter from the generated SpacePageTree shape', () => {
    const tree: SpacePageTreeQuery['pageTree'] = [
      {
        __typename: 'PageTreeNode',
        id: 'root',
        title: 'Root',
        icon: null,
        slug: 'root',
        hasChildren: true,
        sortOrder: 0,
        hasRestrictions: false,
        labels: ['onboarding'],
        marking,
        children: [protectedLeaf],
      },
      protectedLeaf,
    ]
    expect(flattenParentOptions(readableTree(tree)).map((o) => o.id)).toEqual([null, 'root'])
    expect(filterTreeByLabel(readableTree(tree), 'onboarding').map((m) => m.id)).toEqual(['root'])
  })

  it('feeds the move-target flattener from the generated SpaceTreeForMove shape', () => {
    const tree: SpaceTreeForMoveQuery['pageTree'] = [
      { __typename: 'PageTreeNode', id: 'root', title: 'Root', sortOrder: 0, ownViewRestrictions: [], children: [{ __typename: 'ProtectedTreeNode' }] },
      { __typename: 'ProtectedTreeNode' },
    ]
    expect(flattenMoveTargets(readableTree(tree), 'other').map((o) => o.id)).toEqual([null, 'root'])
  })
})
