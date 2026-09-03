import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { profilePath } from '../profilePath'

/**
 * The profile route must not begin with a prefix the dev server forwards to
 * the API. The first version lived at `/users/{id}`, and `/users` is proxied
 * wholesale (web/vite.config.ts `proxyPaths`) because the API owns
 * `GET /users/{id}/avatar` — so client-side navigation reached the page and a
 * direct load or refresh got the API's 404 instead of the SPA. The nginx image
 * forwards the same prefixes. No jsdom test can see a proxy, which is why this
 * one reads the proxy table off disk rather than trusting a comment.
 */
const VITE_CONFIG = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'vite.config.ts')

/** Every path prefix vite.config.ts hands to the API: the `proxyPaths` array plus any entry written straight into `server.proxy` (`/hubs`). */
function proxiedPrefixes(source: string): string[] {
  const array = /const proxyPaths\s*=\s*\[([^\]]*)\]/.exec(source)?.[1] ?? ''
  const fromArray = [...array.matchAll(/'([^']+)'/g)].map((m) => m[1]!)
  const fromObject = [...source.matchAll(/'(\/[^']+)':\s*\{\s*target/g)].map((m) => m[1]!)
  return [...fromArray, ...fromObject]
}

describe('profilePath stays off the API proxy', () => {
  const prefixes = proxiedPrefixes(readFileSync(VITE_CONFIG, 'utf8'))

  it('found the proxy table, so a pass cannot be vacuous', () => {
    // `/users` is the very prefix this guards against; `/graphql` is the one
    // nobody will ever remove. Either missing means the parse broke, not that
    // the proxy went away.
    expect(prefixes).toContain('/users')
    expect(prefixes).toContain('/graphql')
    expect(prefixes).toContain('/hubs')
  })

  it("the profile route's first segment is not one the proxy forwards", () => {
    const path = profilePath('7d2a1c9e-1111-2222-3333-444455556666')
    const firstSegment = `/${path.split('/')[1] ?? ''}`
    expect(
      prefixes,
      `${path} would be swallowed by the API proxy on a direct load or refresh`,
    ).not.toContain(firstSegment)
  })
})
