import { fileURLToPath } from 'node:url'
import path from 'node:path'
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { computeHeadingAnchors } from '../headingAnchors'
import { headingAnchorFixtures } from './headingAnchorsCorpus'

/**
 * Regenerates and asserts the checked-in cross-language corpus at
 * `tests/fixtures/heading-anchors/` (design.md §9) — see
 * headingAnchorsCorpus.ts for why this exists and what it's for. Same
 * "regenerate on every run, then diff" pattern as the importer's
 * CorpusGenerationTests: the checked-in files can never hand-drift from
 * what `headingAnchors.ts` actually produces, because this test rewrites
 * them from it every time.
 */
function corpusDirectory(): string {
  const currentDir = path.dirname(fileURLToPath(import.meta.url))
  // web/src/editor/__tests__ -> repo root is four levels up.
  const dir = path.resolve(currentDir, '../../../../tests/fixtures/heading-anchors')
  mkdirSync(dir, { recursive: true })
  return dir
}

describe('heading-anchors fixture corpus (cross-language contract)', () => {
  const dir = corpusDirectory()

  it.each(headingAnchorFixtures)('regenerates a corpus file for "$name"', (fixture) => {
    const anchors = computeHeadingAnchors(fixture.headings)
    const contents = `${JSON.stringify({ headings: fixture.headings, anchors }, null, 2)}\n`
    const filePath = path.join(dir, `${fixture.name}.json`)

    writeFileSync(filePath, contents, 'utf-8')

    expect(readFileSync(filePath, 'utf-8')).toBe(contents)
  })

  it('fixture names are unique, so no corpus file is silently overwritten by another fixture', () => {
    const names = headingAnchorFixtures.map((f) => f.name)
    expect(new Set(names).size).toBe(names.length)
  })

  it('the corpus directory has no stale files left over from renamed or removed fixtures', () => {
    const expected = new Set(headingAnchorFixtures.map((f) => `${f.name}.json`))
    const actual = existsSync(dir) ? readdirSync(dir).filter((f) => f.endsWith('.json')) : []
    const stale = actual.filter((f) => !expected.has(f))
    expect(stale).toEqual([])
  })
})
