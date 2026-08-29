import { describe, expect, it } from 'vitest'
import { buildFormDefinitionFenceBody, buildFormListFenceBody } from '../fenceBody'

/**
 * The bodies the insert dialog writes. What matters is that the FIRST fence
 * someone inserts is valid — the field syntax is the part nobody guesses — so
 * these are checked against the shape the server's parser actually accepts.
 */
describe('buildFormDefinitionFenceBody', () => {
  it('writes a collection and one line per field', () => {
    const body = buildFormDefinitionFenceBody({
      collection: 'incident-report',
      fields: [
        { name: 'summary', type: 'TEXT', required: true, options: '' },
        { name: 'occurredAt', type: 'DATE', required: false, options: '' },
      ],
    })
    expect(body).toBe('collection = incident-report\nfield = summary: text, required\nfield = occurredAt: date')
  })

  it('writes a choice field with its options', () => {
    const body = buildFormDefinitionFenceBody({
      collection: 'notes',
      fields: [{ name: 'severity', type: 'SELECT', required: true, options: ' low ,medium,  high ' }],
    })
    expect(body).toContain('field = severity: select(low, medium, high), required')
  })

  it('skips a field with no name rather than writing a broken line', () => {
    const body = buildFormDefinitionFenceBody({
      collection: 'notes',
      fields: [
        { name: '', type: 'TEXT', required: false, options: '' },
        { name: 'body', type: 'TEXT', required: false, options: '' },
      ],
    })
    expect(body).toBe('collection = notes\nfield = body: text')
  })

  it('lower-cases the type token, matching what the parser reads', () => {
    const body = buildFormDefinitionFenceBody({
      collection: 'notes',
      fields: [{ name: 'cost', type: 'NUMBER', required: false, options: '' }],
    })
    expect(body).toContain(': number')
  })
})

describe('buildFormListFenceBody', () => {
  it('writes just the collection when nothing else was chosen', () => {
    expect(buildFormListFenceBody({ collection: 'notes', columns: [], where: '' })).toBe('collection = notes')
  })

  it('omits an empty where rather than writing a dangling key', () => {
    // `where =` with nothing after it parses as no filter, but it reads like an
    // unfinished thought sitting in the author's page.
    const body = buildFormListFenceBody({ collection: 'notes', columns: ['a'], where: '   ' })
    expect(body).toBe('collection = notes\ncolumns = a')
  })

  it('writes columns and a filter when given them', () => {
    const body = buildFormListFenceBody({
      collection: 'notes',
      columns: ['severity', 'summary'],
      where: 'severity = high',
    })
    expect(body).toBe('collection = notes\ncolumns = severity, summary\nwhere = severity = high')
  })
})
