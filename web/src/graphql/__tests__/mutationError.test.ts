import { describe, expect, it } from 'vitest'
import type { MutationErrorFragment } from '../generated/graphql'
import {
  asReadOnlyReplica,
  asStaleRevision,
  blockedPageCount,
  describeMutationError,
} from '../mutationError'

/**
 * The API's flattened error shape (PageMutationErrorView): one type, a
 * `kind` discriminator, only that kind's fields non-null. These tests pin
 * the client-side narrowing — the exact seam the reconciliation from the
 * placeholder's per-kind error types moved everything onto.
 */
function error(overrides: Partial<MutationErrorFragment>): MutationErrorFragment {
  return {
    kind: 'Validation',
    message: null,
    expectedRevisionNumber: null,
    actualRevisionNumber: null,
    latestTitle: null,
    latestContent: null,
    spaceId: null,
    originInstanceId: null,
    blockedPageCount: null,
    notFoundId: null,
    ...overrides,
  }
}

describe('asStaleRevision', () => {
  it('carries the latest revision data through for the merge flow', () => {
    const result = asStaleRevision(
      error({
        kind: 'StaleRevision',
        expectedRevisionNumber: 3,
        actualRevisionNumber: 7,
        latestTitle: 'Their title',
        latestContent: 'their content',
      }),
    )
    expect(result).toEqual({
      expectedRevisionNumber: 3,
      actualRevisionNumber: 7,
      latestTitle: 'Their title',
      latestContent: 'their content',
    })
  })

  it('returns null for any other kind, and for no error at all', () => {
    expect(asStaleRevision(error({ kind: 'ReadOnlyReplica' }))).toBeNull()
    expect(asStaleRevision(null)).toBeNull()
    expect(asStaleRevision(undefined)).toBeNull()
  })
})

describe('asReadOnlyReplica', () => {
  it('carries originInstanceId through — the replica dialog needs it for "Replica of <origin> — read-only" (design.md §12)', () => {
    const result = asReadOnlyReplica(error({ kind: 'ReadOnlyReplica', spaceId: 'space-1', originInstanceId: 'LOW' }))
    expect(result).toEqual({ spaceId: 'space-1', originInstanceId: 'LOW' })
  })

  it('returns null for any other kind', () => {
    expect(asReadOnlyReplica(error({ kind: 'StaleRevision' }))).toBeNull()
  })
})

describe('blockedPageCount', () => {
  it('exposes the count — and only the count — of a refused subtree operation (design.md §6.4.1)', () => {
    expect(blockedPageCount(error({ kind: 'SubtreeOperationForbidden', blockedPageCount: 3 }))).toBe(3)
  })

  it('returns null for other kinds so callers can distinguish "not blocked" from "blocked zero"', () => {
    expect(blockedPageCount(error({ kind: 'Forbidden' }))).toBeNull()
  })
})

describe('describeMutationError', () => {
  it('returns null for no error', () => {
    expect(describeMutationError(null)).toBeNull()
    expect(describeMutationError(undefined)).toBeNull()
  })

  it('renders Forbidden with its reason', () => {
    expect(describeMutationError(error({ kind: 'Forbidden', message: 'editor role required' }))).toBe(
      'Not permitted: editor role required',
    )
  })

  it('renders NotFound without leaking whether the thing exists (design.md §6.7)', () => {
    const text = describeMutationError(error({ kind: 'NotFound', notFoundId: 'abc' }))
    expect(text).toBe('Not found.')
    expect(text).not.toContain('abc')
  })

  it('renders Validation with the server message', () => {
    expect(describeMutationError(error({ kind: 'Validation', message: "Label 'x' already exists." }))).toBe(
      "Label 'x' already exists.",
    )
  })

  it('never names blocked pages, only counts them', () => {
    const text = describeMutationError(error({ kind: 'SubtreeOperationForbidden', blockedPageCount: 2 }))
    expect(text).toMatch(/2 pages/)
  })
})
