import { readFileSync, readdirSync } from 'node:fs'
import { dirname, join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * No source file may contain a raw NUL byte.
 *
 * Two have appeared, both through scripted edits that meant to write a
 * separator and wrote the byte itself: `markings/PageMarkingSection.tsx` used
 * `join('<NUL>')` in an `isDirty` comparison, and `pages/SearchPage.tsx`
 * acquired one in a `useEffect` dependency key.
 *
 * The damage is not to the running code — `'\0'` is a perfectly good separator
 * — it is to every tool that reads the file. Git and grep classify a file
 * containing a NUL as **binary**, so it silently drops out of `git diff`, `git
 * grep`, ripgrep, and every editor-wide search. `PageMarkingSection` sat
 * outside two full review sweeps for exactly this reason, and neither reviewer
 * could have known: a file that is invisible to search does not announce
 * itself. That is what makes it worth a standing check rather than a fix.
 *
 * Deliberately a byte-level read, not a UTF-8 one: decoding to a string first
 * would happily carry the NUL through and prove nothing about what git sees.
 */

const SRC = join(dirname(fileURLToPath(import.meta.url)), '..')

/** Source we author. Generated output and fixtures are not ours to police. */
const EXTENSIONS = ['.ts', '.tsx', '.css', '.md', '.graphql', '.json']
const SKIP_DIRS = new Set(['generated', 'node_modules'])

function sourceFiles(dir: string, found: string[] = []): string[] {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!SKIP_DIRS.has(entry.name)) sourceFiles(join(dir, entry.name), found)
      continue
    }
    if (EXTENSIONS.some((ext) => entry.name.endsWith(ext))) found.push(join(dir, entry.name))
  }
  return found
}

describe('source files stay text', () => {
  const files = sourceFiles(SRC)

  it('finds files to check, so a green run is not a vacuous one', () => {
    // A walk that silently matched nothing would pass forever.
    expect(files.length).toBeGreaterThan(200)
  })

  it('contains no NUL bytes anywhere under src/', () => {
    const offenders = files
      .map((path) => {
        const bytes = readFileSync(path)
        const at = bytes.indexOf(0)
        return at === -1 ? null : `${relative(SRC, path).replace(/\\/g, '/')} (byte ${at})`
      })
      .filter((offender): offender is string => offender !== null)

    expect(
      offenders,
      'a NUL byte makes git and grep treat the file as binary, so it disappears from every text search — write \\0 as an escape',
    ).toEqual([])
  })
})
