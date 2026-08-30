import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * `.env.example` is where somebody learns what a variable is for, and it was
 * telling them to put a collector API key in one.
 *
 * Vite inlines every `VITE_*` value into the bundle at build time, so a real
 * key set there is served to every browser that loads the app. Nothing is
 * leaking today — every live `VITE_*` var is public — but the documentation
 * invited it, and a config file that invites a secret eventually receives one.
 *
 * A test rather than a comment because the file is read far more often than it
 * is reviewed, and the warning has to survive the next edit.
 */
const ENV_EXAMPLE = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', '.env.example')
const contents = readFileSync(ENV_EXAMPLE, 'utf8')

describe('.env.example says what a VITE_ variable is', () => {
  it('is the file we think it is', () => {
    // A path that silently stopped resolving would make every assertion below
    // vacuous rather than failing.
    expect(contents).toContain('VITE_OIDC_AUTHORITY')
  })

  it('states that these values reach the browser', () => {
    expect(contents).toContain('EVERY `VITE_*` VALUE IS PUBLIC')
    expect(contents).toMatch(/inlines? them into the JavaScript bundle/i)
  })

  it('says plainly that no secret may go in one', () => {
    expect(contents).toMatch(/MUST NOT go here|may hold a secret/i)
  })

  it('no longer suggests a placeholder that reads like a real key', () => {
    // `changeme` is an instruction to put a live credential there.
    expect(contents).not.toContain('x-otlp-api-key=changeme')
  })

  it('records that the authority has no default', () => {
    expect(contents).toMatch(/VITE_OIDC_AUTHORITY is REQUIRED and has no default/)
  })
})
