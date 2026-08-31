import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { MAX_PAGE_SIZE } from '../adminUsersPaging'
import { FEED_MAX_PAGE_SIZE } from '../../home/feedPaging'

/**
 * No screen may ask a paged field for more than that field allows.
 *
 * This is the guard the audit log did not have. `AuditLogPage` requested
 * `first: 100` from a connection capped at 50, so every query failed at
 * validation and the page never loaded at all — a whole screen, dead, because
 * two numbers in two languages disagreed and nothing compared them. Both are
 * checked in and both are readable from here, so the comparison is a test.
 *
 * Hot Chocolate publishes the cap on each field as `@listSize(assumedSize: N)`,
 * which is its `MaxPageSize`. Exceeding it is an ERROR, not a clamp — unlike
 * `search`, which is hand-rolled and clamps down — so asking for too much is
 * never merely wasteful.
 */
const SCHEMA = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', '..', 'schema.graphql')
const schema = readFileSync(SCHEMA, 'utf8')

/** The `assumedSize` on a named Query field's `@listSize` directive. */
function assumedSizeOf(field: string): number {
  const start = schema.indexOf(`\n  ${field}(`)
  if (start === -1) throw new Error(`no field \`${field}\` in schema.graphql`)
  const match = /assumedSize: (\d+)/.exec(schema.slice(start, start + 800))
  if (!match) throw new Error(`no @listSize on \`${field}\``)
  return Number(match[1])
}

/** What a page source actually asks for, read from its own `PAGE_SIZE` declaration. */
function declaredPageSize(relativePath: string): number {
  const source = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', relativePath), 'utf8')
  const match = /const PAGE_SIZE = (\d+)/.exec(source)
  if (!match) throw new Error(`no PAGE_SIZE in ${relativePath}`)
  return Number(match[1])
}

describe('the schema is the authority on how big a page may be', () => {
  it('finds the fields it is checking, so a green run is not a vacuous one', () => {
    for (const field of ['users', 'auditEvents', 'activityFeed', 'myStaleContent', 'myRecentlyViewed']) {
      expect(assumedSizeOf(field)).toBeGreaterThan(0)
    }
  })

  it('never lets the user roster ask for more than the field allows', () => {
    expect(MAX_PAGE_SIZE).toBeLessThanOrEqual(assumedSizeOf('users'))
  })

  it('never lets the audit log ask for more than the field allows', () => {
    // The original casualty: this is the assertion that was missing.
    expect(declaredPageSize('AuditLogPage.tsx')).toBeLessThanOrEqual(assumedSizeOf('auditEvents'))
  })

  it('never lets a homepage feed ask for more than its connection allows', () => {
    // All three share one ceiling, and it is a COST ceiling as much as a row
    // one: each row costs page-size × object-fields, so 20 is what the pinned
    // feed selection affords.
    for (const field of ['activityFeed', 'myStaleContent', 'myRecentlyViewed']) {
      expect(FEED_MAX_PAGE_SIZE).toBeLessThanOrEqual(assumedSizeOf(field))
    }
  })
})
