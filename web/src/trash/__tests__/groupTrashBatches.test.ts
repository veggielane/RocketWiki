import { describe, expect, it } from 'vitest'
import { groupTrashBatches, type TrashedPage } from '../groupTrashBatches'

function page(overrides: Partial<TrashedPage> = {}): TrashedPage {
  return {
    id: 'p1',
    title: 'Page',
    ancestorPath: '/root/',
    deleteBatchId: 'batch-1',
    deletedAtUtc: '2026-08-01T00:00:00Z',
    ...overrides,
  }
}

describe('groupTrashBatches', () => {
  it('groups pages sharing a deleteBatchId into one batch (design.md §6.4.1: one operation, one entry)', () => {
    const batches = groupTrashBatches([
      page({ id: 'root', title: 'Root', ancestorPath: '/' }),
      page({ id: 'child', title: 'Child', ancestorPath: '/root/' }),
      page({ id: 'grandchild', title: 'Grandchild', ancestorPath: '/root/child/' }),
    ])
    expect(batches).toHaveLength(1)
    expect(batches[0].pageCount).toBe(3)
  })

  it('picks the page with the shortest ancestorPath as the batch root — ancestors sort before descendants', () => {
    const batches = groupTrashBatches([
      page({ id: 'grandchild', title: 'Grandchild', ancestorPath: '/root/child/' }),
      page({ id: 'root', title: 'Root', ancestorPath: '/' }),
      page({ id: 'child', title: 'Child', ancestorPath: '/root/' }),
    ])
    expect(batches[0].rootPageId).toBe('root')
    expect(batches[0].rootPageTitle).toBe('Root')
  })

  it('treats a page with no batch stamp as its own single-page batch', () => {
    const batches = groupTrashBatches([
      page({ id: 'a', deleteBatchId: null }),
      page({ id: 'b', deleteBatchId: null }),
    ])
    expect(batches).toHaveLength(2)
    expect(batches.every((b) => b.pageCount === 1)).toBe(true)
  })

  it('computes the 30-day expiry from deletedAtUtc (data-model.md trash window)', () => {
    const batches = groupTrashBatches([page({ deletedAtUtc: '2026-08-01T00:00:00Z' })])
    expect(batches[0].expiresAtUtc).toBe('2026-08-31T00:00:00.000Z')
  })

  it('sorts batches newest deletion first', () => {
    const batches = groupTrashBatches([
      page({ id: 'old', deleteBatchId: 'b-old', deletedAtUtc: '2026-07-01T00:00:00Z' }),
      page({ id: 'new', deleteBatchId: 'b-new', deletedAtUtc: '2026-08-01T00:00:00Z' }),
    ])
    expect(batches.map((b) => b.id)).toEqual(['b-new', 'b-old'])
  })

  it('handles a missing deletedAtUtc without inventing an expiry', () => {
    const batches = groupTrashBatches([page({ deletedAtUtc: null })])
    expect(batches[0].expiresAtUtc).toBeNull()
  })
})
