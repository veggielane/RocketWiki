import { describe, expect, it } from 'vitest'
import { computeHeadingAnchors, computeHeadingPaths, slugifyPath, type HeadingInfo } from '../headingAnchors'

describe('computeHeadingPaths', () => {
  it('a single top-level heading has a path of just itself', () => {
    const headings: HeadingInfo[] = [{ level: 1, text: 'Intro' }]
    expect(computeHeadingPaths(headings)).toEqual([['Intro']])
  })

  it('nests a subheading under its parent', () => {
    const headings: HeadingInfo[] = [
      { level: 1, text: 'Intro' },
      { level: 2, text: 'Setup' },
    ]
    expect(computeHeadingPaths(headings)).toEqual([['Intro'], ['Intro', 'Setup']])
  })

  it('pops back to the correct ancestor after a deeper subsection ends', () => {
    const headings: HeadingInfo[] = [
      { level: 1, text: 'Intro' },
      { level: 2, text: 'Setup' },
      { level: 3, text: 'Windows' },
      { level: 2, text: 'Usage' }, // sibling of "Setup", not nested under it
    ]
    const paths = computeHeadingPaths(headings)
    expect(paths).toEqual([['Intro'], ['Intro', 'Setup'], ['Intro', 'Setup', 'Windows'], ['Intro', 'Usage']])
  })

  it('a new H1 resets the ancestor stack entirely', () => {
    const headings: HeadingInfo[] = [
      { level: 1, text: 'Chapter 1' },
      { level: 2, text: 'Section A' },
      { level: 1, text: 'Chapter 2' },
    ]
    const paths = computeHeadingPaths(headings)
    expect(paths[2]).toEqual(['Chapter 2'])
  })

  it('a heading skipping levels (H1 then H3) still nests under the H1', () => {
    const headings: HeadingInfo[] = [
      { level: 1, text: 'Intro' },
      { level: 3, text: 'Deep detail' },
    ]
    expect(computeHeadingPaths(headings)).toEqual([['Intro'], ['Intro', 'Deep detail']])
  })
})

describe('slugifyPath', () => {
  it('lowercases and hyphenates', () => {
    expect(slugifyPath(['Getting Started'])).toBe('getting-started')
  })

  it('joins a multi-segment path with a double hyphen separator', () => {
    expect(slugifyPath(['Intro', 'Setup', 'Windows'])).toBe('intro--setup--windows')
  })

  it('strips punctuation', () => {
    expect(slugifyPath(["What's New?"])).toBe('whats-new')
  })

  it('falls back to "section" for a heading with no alphanumeric content', () => {
    expect(slugifyPath(['---'])).toBe('section')
  })
})

describe('computeHeadingAnchors — stability and disambiguation', () => {
  it('gives unrelated headings distinct, stable anchors', () => {
    const headings: HeadingInfo[] = [
      { level: 1, text: 'Intro' },
      { level: 1, text: 'Usage' },
    ]
    expect(computeHeadingAnchors(headings)).toEqual(['intro', 'usage'])
  })

  it('is unaffected by inserting an unrelated heading elsewhere', () => {
    const before: HeadingInfo[] = [
      { level: 1, text: 'Intro' },
      { level: 1, text: 'Usage' },
    ]
    const after: HeadingInfo[] = [
      { level: 1, text: 'Intro' },
      { level: 1, text: 'A New Section' },
      { level: 1, text: 'Usage' },
    ]
    const beforeAnchors = computeHeadingAnchors(before)
    const afterAnchors = computeHeadingAnchors(after)
    expect(beforeAnchors[0]).toBe('intro')
    expect(afterAnchors[0]).toBe('intro')
    expect(afterAnchors[2]).toBe('usage') // still "usage", not shifted by the insertion
  })

  it('does not disambiguate two headings that merely share a leaf name under different parents', () => {
    // "Project A > Overview" and "Project B > Overview" are different full
    // paths — only the disambiguation-worthy case is an *identical* path.
    const headings: HeadingInfo[] = [
      { level: 1, text: 'Project A' },
      { level: 2, text: 'Overview' },
      { level: 1, text: 'Project B' },
      { level: 2, text: 'Overview' },
    ]
    const anchors = computeHeadingAnchors(headings)
    expect(anchors).toEqual(['project-a', 'project-a--overview', 'project-b', 'project-b--overview'])
  })

  it('disambiguates two headings with the exact same path — first gets the bare slug', () => {
    const headings: HeadingInfo[] = [
      { level: 1, text: 'Project A' },
      { level: 2, text: 'Overview' },
      { level: 2, text: 'Overview' }, // sibling with the identical text and path
    ]
    const anchors = computeHeadingAnchors(headings)
    expect(anchors).toEqual(['project-a', 'project-a--overview', 'project-a--overview-2'])
  })

  it('duplicate top-level headings disambiguate independently of nested duplicates', () => {
    const headings: HeadingInfo[] = [
      { level: 1, text: 'Overview' },
      { level: 1, text: 'Overview' },
      { level: 1, text: 'Overview' },
    ]
    expect(computeHeadingAnchors(headings)).toEqual(['overview', 'overview-2', 'overview-3'])
  })

  it('a duplicate-path disambiguation ordinal does not leak into unrelated slugs', () => {
    const headings: HeadingInfo[] = [
      { level: 1, text: 'Overview' },
      { level: 1, text: 'Overview' },
      { level: 1, text: 'Setup' },
    ]
    expect(computeHeadingAnchors(headings)).toEqual(['overview', 'overview-2', 'setup'])
  })
})
