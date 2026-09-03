import { describe, expect, it } from 'vitest'
import { filterTreeByLabel, type LabeledTreeNode } from '../filterTreeByLabel'
import { readableTree } from '../../pages/treeEntries'
import type { SpacePageTreeQuery } from '../../graphql/generated/graphql'

describe('filterTreeByLabel', () => {
  it('returns nothing for an empty tree', () => {
    expect(filterTreeByLabel([], 'onboarding')).toEqual([])
  })

  it('matches a top-level page with no ancestors', () => {
    const tree: LabeledTreeNode[] = [{ id: 'a', title: 'Getting Started', labels: ['onboarding'] }]
    expect(filterTreeByLabel(tree, 'onboarding')).toEqual([{ id: 'a', title: 'Getting Started', path: [] }])
  })

  it('excludes pages without the label', () => {
    const tree: LabeledTreeNode[] = [
      { id: 'a', title: 'Has it', labels: ['onboarding'] },
      { id: 'b', title: 'Does not', labels: ['reference'] },
    ]
    expect(filterTreeByLabel(tree, 'onboarding').map((m) => m.id)).toEqual(['a'])
  })

  it('builds a root-first breadcrumb path for a nested match', () => {
    const tree: LabeledTreeNode[] = [
      {
        id: 'top',
        title: 'Engineering',
        labels: [],
        children: [
          {
            id: 'mid',
            title: 'Onboarding',
            labels: [],
            children: [{ id: 'leaf', title: 'Day One', labels: ['onboarding'] }],
          },
        ],
      },
    ]
    const matches = filterTreeByLabel(tree, 'onboarding')
    expect(matches).toEqual([{ id: 'leaf', title: 'Day One', path: ['Engineering', 'Onboarding'] }])
  })

  it('matches an ancestor and a descendant independently, each with its own path', () => {
    const tree: LabeledTreeNode[] = [
      {
        id: 'parent',
        title: 'Parent',
        labels: ['shared'],
        children: [{ id: 'child', title: 'Child', labels: ['shared'] }],
      },
    ]
    const matches = filterTreeByLabel(tree, 'shared')
    expect(matches).toEqual([
      { id: 'parent', title: 'Parent', path: [] },
      { id: 'child', title: 'Child', path: ['Parent'] },
    ])
  })

  it('finds matches across multiple sibling subtrees', () => {
    const tree: LabeledTreeNode[] = [
      { id: 'a', title: 'A', labels: [], children: [{ id: 'a1', title: 'A1', labels: ['x'] }] },
      { id: 'b', title: 'B', labels: [], children: [{ id: 'b1', title: 'B1', labels: ['x'] }] },
    ]
    expect(filterTreeByLabel(tree, 'x').map((m) => m.id)).toEqual(['a1', 'b1'])
  })

  it('accepts the generated SpacePageTree shape through readableTree', () => {
    // Compile-time round-trip against the REAL generated type
    // (`PageTreeNode.labels` landed in the schema): if the operation's
    // shape drifts from what readableTree hands LabeledTreeNode, this stops
    // compiling. The tree is a union now (a protected page is a placeholder
    // with no labels), so the readable projection is the adaptation.
    const apiNode: SpacePageTreeQuery['pageTree'][number] = {
      __typename: 'PageTreeNode',
      id: 'api-a',
      title: 'From API',
      // `icon` and `hasChildren` joined the TreeNode fragment when the rail
      // gained per-page glyphs and truncation-honest disclosure; this fixture
      // is a compile-time assertion against the real generated type, so it has
      // to carry them.
      icon: null,
      hasChildren: false,
      slug: 'from-api',
      sortOrder: 0,
      hasRestrictions: false,
      labels: ['onboarding'],
      marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], ukPrefix: true, selectors: [], label: 'UK OFFICIAL' },
      children: [],
    }
    const placeholder: SpacePageTreeQuery['pageTree'][number] = {
      __typename: 'ProtectedTreeNode',
      title: '(protected)',
      sortOrder: 1,
      denial: { placeholderTitle: '(protected)', noSpaceAccess: false, marking: null, reasons: [] },
    }
    expect(filterTreeByLabel(readableTree([apiNode, placeholder]), 'onboarding').map((m) => m.id)).toEqual(['api-a'])
  })
})
