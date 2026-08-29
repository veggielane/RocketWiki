import { describe, expect, it } from 'vitest'
import { lookupPageIcon, PAGE_ICON_OPTIONS } from '../pageIcons'

describe('page icon registry', () => {
  it('resolves an icon this build knows to a glyph and a label', () => {
    const rocket = lookupPageIcon('ROCKET')
    expect(rocket?.label).toBe('Rocket')
    expect(rocket?.Icon).toBeDefined()
  })

  it('has nothing to say about a page with no icon', () => {
    // The ordinary case — nullable everywhere on the wire.
    expect(lookupPageIcon(null)).toBeUndefined()
    expect(lookupPageIcon(undefined)).toBeUndefined()
  })

  it('degrades rather than throwing on a name from a newer icon set', () => {
    // design.md §12: a page can arrive in a sync bundle from an instance whose
    // icon set is ahead of this build's. The caller falls back to its own
    // glyph; nothing here may explode.
    expect(lookupPageIcon('SATELLITE')).toBeUndefined()
  })

  it('is not fooled by a name that happens to be an object property', () => {
    // A plain object lookup would answer truthily for these and hand the
    // caller a function where an icon entry was expected.
    expect(lookupPageIcon('constructor')).toBeUndefined()
    expect(lookupPageIcon('toString')).toBeUndefined()
  })

  it('offers each icon once, with a label the picker can name it by', () => {
    const values = PAGE_ICON_OPTIONS.map((entry) => entry.value)
    expect(new Set(values).size).toBe(values.length)
    expect(PAGE_ICON_OPTIONS.every((entry) => entry.label.trim().length > 0)).toBe(true)
  })
})
