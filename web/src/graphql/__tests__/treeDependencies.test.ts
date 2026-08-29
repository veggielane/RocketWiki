import { fileURLToPath } from 'node:url'
import path from 'node:path'
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { PAGE_TREE_DEPENDENCIES } from '../treeDependencies'

const schemaPath = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '../../../../schema.graphql',
)
const schema = readFileSync(schemaPath, 'utf8')

/**
 * These strings are only ever compared against a `__typename` at runtime, so a
 * renamed or deleted type turns an entry into a no-op that still looks right.
 * Nothing else in the app would notice: the tree would simply go stale again,
 * which is precisely the bug this list exists to fix and precisely the symptom
 * nobody attributes to a cache.
 */
describe('PAGE_TREE_DEPENDENCIES', () => {
  it('names types that really exist in the schema', () => {
    for (const typename of PAGE_TREE_DEPENDENCIES) {
      expect(schema, `${typename} is not a type in schema.graphql`).toMatch(
        new RegExp(`^type ${typename} \\{`, 'm'),
      )
    }
  })

  it('covers the payload of every mutation that changes what the tree draws', () => {
    // Derived from the schema rather than restated, so adding a tree-affecting
    // mutation without listing its payload type fails here instead of shipping
    // a sidebar that quietly stops updating.
    const mutations = ['createPage', 'movePage', 'deletePage', 'restorePage', 'attachLabel', 'setPageMarking']
    for (const field of mutations) {
      const signature = new RegExp(`^\\s*${field}\\(.*\\): (\\w+)!`, 'm').exec(schema)
      expect(signature, `${field} is missing from schema.graphql`).not.toBeNull()

      const payloadType = signature![1]!
      const body = new RegExp(`^type ${payloadType} \\{([\\s\\S]*?)^\\}`, 'm').exec(schema)
      expect(body, `${payloadType} is not a type in schema.graphql`).not.toBeNull()

      // The payload's non-error fields are what the cache actually sees come
      // back; at least one of them must be a type the tree has declared.
      const returned = [...body![1]!.matchAll(/:\s*\[?(\w+)/g)].map((m) => m[1]!)
      expect(
        returned.some((t) => (PAGE_TREE_DEPENDENCIES as readonly string[]).includes(t)),
        `${field} returns ${returned.join('/')}, none of which the page tree declares — ` +
          'the tree will not refresh after it.',
      ).toBe(true)
    }
  })
})
