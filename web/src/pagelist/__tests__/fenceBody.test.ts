import { describe, expect, it } from 'vitest'
import { buildPageListFenceBody, parsePageListFence, type PageListSpec } from '../fenceBody'

describe('page-list fence body', () => {
  it('parses query and limit', () => {
    expect(parsePageListFence('query = label = "safety"\nlimit = 20')).toEqual({
      ok: true,
      spec: { query: 'label = "safety"', limit: 20 },
    })
  })

  // The whole reason this format works for RQL: the shared parser splits at
  // the FIRST `=`, so every later one belongs to the query.
  it('keeps every equals sign after the first inside the query', () => {
    const query = '(label = "a" OR label = "b") AND NOT label = "archived" ORDER BY updated DESC'
    expect(parsePageListFence(`query = ${query}`)).toEqual({ ok: true, spec: { query } })
  })

  it('limit is optional', () => {
    expect(parsePageListFence('query = label = "safety"')).toEqual({
      ok: true,
      spec: { query: 'label = "safety"' },
    })
  })

  it('a non-numeric or zero limit is treated as unset, not as an error', () => {
    // `first` must be an Int on the wire, so there is nothing else to send —
    // the same call the GitLab `first=` key makes.
    expect(parsePageListFence('query = label = "a"\nlimit = lots')).toEqual({ ok: true, spec: { query: 'label = "a"' } })
    expect(parsePageListFence('query = label = "a"\nlimit = 0')).toEqual({ ok: true, spec: { query: 'label = "a"' } })
  })

  it('reports the missing query instead of guessing one', () => {
    expect(parsePageListFence('limit = 5')).toEqual({ ok: false, missing: ['query'] })
    expect(parsePageListFence('this is not key=value')).toEqual({ ok: false, missing: ['query'] })
  })

  // Never validated client-side: §22.4 puts the grammar and its vocabulary on
  // the server, and a second opinion here would be a second door into it.
  it('an invalid RQL query parses fine as a fence — validity is the server’s call', () => {
    expect(parsePageListFence('query = marking = SECRET')).toEqual({
      ok: true,
      spec: { query: 'marking = SECRET' },
    })
  })

  it('unknown keys are ignored, never an error (forward compatibility)', () => {
    expect(parsePageListFence('query = label = "a"\ngroupBy = space')).toEqual({
      ok: true,
      spec: { query: 'label = "a"' },
    })
  })

  it('parse ∘ build is the identity', () => {
    const specs: PageListSpec[] = [
      { query: 'label = "safety"' },
      { query: 'label IN ("draft", "review") AND space IN ("ENG", "OPS")', limit: 20 },
      { query: 'creator = currentUser() AND updated > now("-7d") ORDER BY updated DESC', limit: 1 },
    ]
    for (const spec of specs) {
      expect(parsePageListFence(buildPageListFenceBody(spec))).toEqual({ ok: true, spec })
    }
  })
})
