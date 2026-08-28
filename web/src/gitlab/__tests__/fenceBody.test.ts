import { describe, expect, it } from 'vitest'
import { buildFileFenceBody, buildIssuesFenceBody, parseFileFence, parseIssuesFence } from '../fenceBody'

// The `key=value` line format itself is shared with the page-list fence and
// is tested at its own home: format/__tests__/keyValueBody.test.ts.

describe('gitlab-file fence body', () => {
  it('parses project/path/ref', () => {
    expect(parseFileFence('project=propulsion/turbopump\npath=docs/spec.md\nref=main')).toEqual({
      ok: true,
      ref: { project: 'propulsion/turbopump', path: 'docs/spec.md', ref: 'main' },
    })
  })

  it('ref is optional (server defaults to HEAD)', () => {
    expect(parseFileFence('project=142\npath=README.md')).toEqual({
      ok: true,
      ref: { project: '142', path: 'README.md' },
    })
  })

  it('reports missing required keys instead of guessing', () => {
    expect(parseFileFence('ref=main')).toEqual({ ok: false, missing: ['project', 'path'] })
    expect(parseFileFence('project=142')).toEqual({ ok: false, missing: ['path'] })
  })

  it('unknown keys are ignored, never an error (forward compatibility)', () => {
    expect(parseFileFence('project=142\npath=a.md\nfuture=thing')).toEqual({
      ok: true,
      ref: { project: '142', path: 'a.md' },
    })
  })

  it('parse ∘ build is the identity', () => {
    for (const ref of [
      { project: '142', path: 'README.md' },
      { project: 'g/p', path: 'docs/spec.md', ref: 'v2.1' },
    ]) {
      expect(parseFileFence(buildFileFenceBody(ref))).toEqual({ ok: true, ref })
    }
  })
})

describe('gitlab-issues fence body', () => {
  it('parses the full filter vocabulary', () => {
    expect(
      parseIssuesFence(
        'project=g/p\nstate=opened\nlabels=bug, priority::high\nsearch=vibration\nmilestone=v2\norderBy=updated_at\nsort=desc\nfirst=10',
      ),
    ).toEqual({
      ok: true,
      spec: {
        project: 'g/p',
        state: 'opened',
        labels: ['bug', 'priority::high'],
        search: 'vibration',
        milestone: 'v2',
        orderBy: 'updated_at',
        sort: 'desc',
        first: 10,
      },
    })
  })

  it('project alone is enough', () => {
    expect(parseIssuesFence('project=142')).toEqual({ ok: true, spec: { project: '142' } })
  })

  it('missing project is the one local error', () => {
    expect(parseIssuesFence('state=opened')).toEqual({ ok: false, missing: ['project'] })
  })

  it('out-of-vocabulary filter strings travel verbatim — degrading them is the server’s job (§18)', () => {
    const parsed = parseIssuesFence('project=142\nstate=banana\norderBy=chaos')
    expect(parsed).toEqual({ ok: true, spec: { project: '142', state: 'banana', orderBy: 'chaos' } })
  })

  it('non-numeric first is treated as unset (it must be an Int on the wire)', () => {
    const parsed = parseIssuesFence('project=142\nfirst=lots')
    expect(parsed).toEqual({ ok: true, spec: { project: '142' } })
  })

  it('empty labels CSV yields no labels key', () => {
    expect(parseIssuesFence('project=142\nlabels= , ,')).toEqual({ ok: true, spec: { project: '142' } })
  })

  it('parse ∘ build is the identity', () => {
    for (const spec of [
      { project: '142' },
      { project: 'g/p', state: 'closed', labels: ['bug', 'ops'], search: 'pump', first: 5 },
      { project: 'g/p', milestone: 'v2', orderBy: 'created_at', sort: 'asc' },
    ]) {
      expect(parseIssuesFence(buildIssuesFenceBody(spec))).toEqual({ ok: true, spec })
    }
  })
})
