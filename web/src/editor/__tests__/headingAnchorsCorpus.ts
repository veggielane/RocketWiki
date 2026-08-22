import type { HeadingInfo } from '../headingAnchors'

export interface HeadingAnchorFixture {
  name: string
  headings: HeadingInfo[]
}

/**
 * design.md §9: the anchor algorithm is a cross-language contract — the
 * server must compute `SearchHit.anchorId` with the exact same algorithm
 * as `headingAnchors.ts`, since only the server sees a page's full heading
 * list and can disambiguate repeated headings. This is the source-of-truth
 * fixture list; `headingAnchorsCorpus.test.ts` regenerates
 * `tests/fixtures/heading-anchors/{name}.json` from it on every run (same
 * "regenerate, don't hand-maintain" pattern as the importer's
 * `ConversionCorpus` — see tests/RocketWiki.Importer.Tests/Corpus). A
 * future backend implementation ports the algorithm and asserts its own
 * output against these same checked-in files, which turns "must match
 * exactly" from an instruction into a test that fails if it doesn't.
 */
export const headingAnchorFixtures: HeadingAnchorFixture[] = [
  {
    name: 'single-top-level-heading',
    headings: [{ level: 1, text: 'Intro' }],
  },
  {
    name: 'nested-heading',
    headings: [
      { level: 1, text: 'Intro' },
      { level: 2, text: 'Setup' },
    ],
  },
  {
    name: 'sibling-headings-same-level',
    headings: [
      { level: 1, text: 'Intro' },
      { level: 1, text: 'Usage' },
    ],
  },
  {
    name: 'pops-back-to-correct-ancestor',
    headings: [
      { level: 1, text: 'Intro' },
      { level: 2, text: 'Setup' },
      { level: 3, text: 'Windows' },
      { level: 2, text: 'Usage' },
    ],
  },
  {
    name: 'new-h1-resets-ancestor-stack',
    headings: [
      { level: 1, text: 'Chapter 1' },
      { level: 2, text: 'Section A' },
      { level: 1, text: 'Chapter 2' },
    ],
  },
  {
    name: 'level-skip-h1-to-h3',
    headings: [
      { level: 1, text: 'Intro' },
      { level: 3, text: 'Deep Detail' },
    ],
  },
  {
    name: 'punctuation-and-case',
    headings: [{ level: 1, text: "What's New? (v2.0!)" }],
  },
  {
    name: 'only-punctuation-heading-falls-back-to-section',
    headings: [{ level: 1, text: '---???---' }],
  },
  {
    name: 'empty-heading-text-falls-back-to-section',
    headings: [{ level: 1, text: '' }],
  },
  {
    name: 'non-ascii-heading-strips-to-section',
    // Known, documented behavior: the slugifier only keeps [a-z0-9\s-],
    // so a heading with no ASCII alphanumeric content falls back to the
    // generic "section" anchor rather than transliterating. Two such
    // headings on one page would collide and disambiguate like any other
    // duplicate path (see duplicate-exact-path-two-headings below).
    headings: [{ level: 1, text: '概要' }],
  },
  {
    name: 'duplicate-exact-path-two-headings',
    headings: [
      { level: 1, text: 'Project A' },
      { level: 2, text: 'Overview' },
      { level: 2, text: 'Overview' },
    ],
  },
  {
    name: 'duplicate-exact-path-three-headings',
    headings: [
      { level: 1, text: 'Overview' },
      { level: 1, text: 'Overview' },
      { level: 1, text: 'Overview' },
    ],
  },
  {
    name: 'same-leaf-name-different-parents-is-not-a-collision',
    headings: [
      { level: 1, text: 'Project A' },
      { level: 2, text: 'Overview' },
      { level: 1, text: 'Project B' },
      { level: 2, text: 'Overview' },
    ],
  },
  {
    name: 'unrelated-heading-inserted-between-does-not-shift-anchors',
    headings: [
      { level: 1, text: 'Intro' },
      { level: 1, text: 'A New Section' },
      { level: 1, text: 'Usage' },
    ],
  },
]
