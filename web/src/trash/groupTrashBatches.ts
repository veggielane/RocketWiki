/**
 * design.md §6.4.1: a cascade delete trashes a whole subtree as one batch,
 * and the trash UI lists batches, not one row per page. The real schema
 * exposes trashed *pages* (each stamped with its `deleteBatchId`), so this
 * groups them back into batches client-side. The batch root is the page
 * with the shortest `ancestorPath` in its group (ancestors sort before
 * descendants); restoring by that root's page id restores the whole batch
 * (RestorePageAsync fans out over the batch server-side).
 *
 * The 30-day expiry (data-model.md) is computed here from `deletedAtUtc`
 * because the server doesn't send a precomputed `expiresAtUtc` — if that
 * policy ever becomes configurable server-side, this constant must go with
 * it (flagged in the reconciliation report rather than silently owned).
 */

export interface TrashedPage {
  id: string
  title: string
  ancestorPath: string
  deleteBatchId: string | null
  deletedAtUtc: string | null
}

export interface TrashBatch {
  /** The batch id, or the page's own id for a legacy row with no batch stamp. */
  id: string
  rootPageId: string
  rootPageTitle: string
  pageCount: number
  deletedAtUtc: string | null
  expiresAtUtc: string | null
}

const TRASH_WINDOW_DAYS = 30

function expiryOf(deletedAtUtc: string | null): string | null {
  if (!deletedAtUtc) return null
  const deleted = new Date(deletedAtUtc)
  if (Number.isNaN(deleted.getTime())) return null
  return new Date(deleted.getTime() + TRASH_WINDOW_DAYS * 24 * 60 * 60 * 1000).toISOString()
}

export function groupTrashBatches(pages: TrashedPage[]): TrashBatch[] {
  const byBatch = new Map<string, TrashedPage[]>()
  for (const page of pages) {
    const key = page.deleteBatchId ?? page.id
    const group = byBatch.get(key)
    if (group) {
      group.push(page)
    } else {
      byBatch.set(key, [page])
    }
  }

  const batches: TrashBatch[] = []
  for (const [id, group] of byBatch) {
    const root = group.reduce((a, b) => (b.ancestorPath.length < a.ancestorPath.length ? b : a))
    batches.push({
      id,
      rootPageId: root.id,
      rootPageTitle: root.title,
      pageCount: group.length,
      deletedAtUtc: root.deletedAtUtc,
      expiresAtUtc: expiryOf(root.deletedAtUtc),
    })
  }

  // Newest deletions first, matching the server's own trashedPages ordering.
  batches.sort((a, b) => (b.deletedAtUtc ?? '').localeCompare(a.deletedAtUtc ?? ''))
  return batches
}
