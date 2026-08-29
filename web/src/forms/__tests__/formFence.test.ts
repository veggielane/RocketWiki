import { describe, expect, it } from 'vitest'
import { parseFormFence } from '../formFence'

/**
 * The fence body, as the SPA reads it — which is deliberately almost nothing.
 * The fields themselves are parsed by the server and handed back, so rendering
 * and validation cannot become two opinions about what a form is. What is left
 * here is "which collection, which columns".
 */
describe('parseFormFence', () => {
  it('reads a collection', () => {
    const result = parseFormFence('collection = incident-report')
    expect(result).toEqual({ ok: true, spec: { collection: 'incident-report', columns: [] } })
  })

  it('reads columns as an ordered list', () => {
    const result = parseFormFence('collection = incident-report\ncolumns = occurredAt, severity , summary')
    expect(result.ok && result.spec.columns).toEqual(['occurredAt', 'severity', 'summary'])
  })

  it('treats no columns as "every field the definition declares"', () => {
    // Empty rather than a sentinel: the blocks read an empty list as "all", so a
    // fence with no columns line and one with an empty one behave identically.
    expect(parseFormFence('collection = notes').ok && parseFormFence('collection = notes').spec.columns).toEqual([])
    const blank = parseFormFence('collection = notes\ncolumns =   ')
    expect(blank.ok && blank.spec.columns).toEqual([])
  })

  it('reports a missing collection as incomplete, not as an error', () => {
    // A half-typed fence in the editor should read as unfinished, which is the
    // state page-list shows for the same reason — an author mid-keystroke has
    // not made a mistake yet.
    expect(parseFormFence('columns = a, b')).toEqual({ ok: false, missing: ['collection'] })
    expect(parseFormFence('')).toEqual({ ok: false, missing: ['collection'] })
    expect(parseFormFence('collection =   ')).toEqual({ ok: false, missing: ['collection'] })
  })

  it('ignores keys it does not know', () => {
    // A fence written by a newer build degrades on an older one rather than
    // breaking the page it sits on.
    const result = parseFormFence('collection = notes\nsomethingLater = 42')
    expect(result.ok && result.spec.collection).toBe('notes')
  })
})
