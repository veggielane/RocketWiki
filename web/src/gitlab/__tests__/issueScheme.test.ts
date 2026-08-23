import { describe, expect, it } from 'vitest'
import {
  formatGitLabIssueTarget,
  parseGitLabIssueTarget,
  parseGitLabIssueUrl,
} from '../issueScheme'

describe('parseGitLabIssueTarget', () => {
  it('splits at the final numeric segment: numeric project id', () => {
    expect(parseGitLabIssueTarget('gitlab-issue://142/57')).toEqual({ project: '142', iid: '57' })
  })

  it('splits at the final numeric segment: namespaced path keeps its slashes', () => {
    expect(parseGitLabIssueTarget('gitlab-issue://group/subgroup/proj/57')).toEqual({
      project: 'group/subgroup/proj',
      iid: '57',
    })
  })

  it('a project path whose segments are numeric still yields the FINAL segment as iid', () => {
    expect(parseGitLabIssueTarget('gitlab-issue://a/12/34')).toEqual({ project: 'a/12', iid: '34' })
  })

  it('keeps iid digits verbatim (leading zeros are the author’s bytes)', () => {
    expect(parseGitLabIssueTarget('gitlab-issue://legacy/007')).toEqual({ project: 'legacy', iid: '007' })
  })

  const malformed = [
    'gitlab-issue://proj/abc',
    'gitlab-issue://proj/57x',
    'gitlab-issue://proj/57/',
    'gitlab-issue://57',
    'gitlab-issue:///57',
    'gitlab-issue://',
    'page://proj/57',
    'https://gitlab.example.com/proj/-/issues/57',
  ]
  for (const href of malformed) {
    it(`rejects ${JSON.stringify(href)}`, () => {
      expect(parseGitLabIssueTarget(href)).toBeNull()
    })
  }

  it('format ∘ parse is the identity on well-formed targets', () => {
    for (const href of ['gitlab-issue://142/57', 'gitlab-issue://group/sub/proj/999', 'gitlab-issue://legacy/007']) {
      expect(formatGitLabIssueTarget(parseGitLabIssueTarget(href)!)).toBe(href)
    }
  })
})

describe('parseGitLabIssueUrl (explicit paste-time conversion, host stripped)', () => {
  it('modern /-/issues/ URL', () => {
    expect(parseGitLabIssueUrl('https://gitlab.example.com/propulsion/turbopump/-/issues/57')).toEqual({
      project: 'propulsion/turbopump',
      iid: '57',
    })
  })

  it('legacy /issues/ URL (no /-/)', () => {
    expect(parseGitLabIssueUrl('https://gitlab.example.com/group/proj/issues/57')).toEqual({
      project: 'group/proj',
      iid: '57',
    })
  })

  it('deep namespaces survive; trailing slash and query/fragment noise are ignored', () => {
    expect(parseGitLabIssueUrl('https://gl.local/a/b/c/-/issues/12/?foo=1#note_3')).toEqual({
      project: 'a/b/c',
      iid: '12',
    })
  })

  it('the host never reaches the result (that is the point of the scheme form)', () => {
    const ref = parseGitLabIssueUrl('https://secret-host.example.com/g/p/-/issues/5')
    expect(JSON.stringify(ref)).not.toContain('secret-host')
  })

  const rejected = [
    'not a url at all',
    'https://gitlab.example.com/group/proj',
    'https://gitlab.example.com/group/proj/-/merge_requests/57',
    'https://gitlab.example.com/group/proj/-/issues/notanumber',
    'https://gitlab.example.com/issues/57',
  ]
  for (const url of rejected) {
    it(`rejects ${JSON.stringify(url)}`, () => {
      expect(parseGitLabIssueUrl(url)).toBeNull()
    })
  }
})
