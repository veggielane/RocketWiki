import { describe, expect, it } from 'vitest'
import { citationHref, segmentAnswer, type AskCitation } from '../answerSegments'

/**
 * The `[Sn]` marker transform (design.md §9.5): n is 1-based into the
 * citations array. The transform is the ONLY interpretation applied to the
 * (untrusted) answer text, so its edge behavior is load-bearing: anything
 * that isn't a resolvable marker must survive verbatim as plain text.
 */

const c = (n: number): AskCitation => ({
  pageId: `page-${n}`,
  title: `Title ${n}`,
  headingPath: [`H${n}`],
  anchorId: `anchor-${n}`,
  // §21.13: each citation carries its source page's own marking. Nothing in
  // the marker transform touches it — it is here because a citation is not a
  // citation without it.
  marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], ukPrefix: true, selectors: [], label: 'UK OFFICIAL' },
})

const CITATIONS = [c(1), c(2), c(3)]

describe('segmentAnswer', () => {
  it('an answer without markers is a single text segment', () => {
    expect(segmentAnswer('Plain answer.', CITATIONS)).toEqual([{ kind: 'text', text: 'Plain answer.' }])
  })

  it('resolves a single mid-text marker', () => {
    expect(segmentAnswer('The impeller is titanium [S1] as specified.', CITATIONS)).toEqual([
      { kind: 'text', text: 'The impeller is titanium ' },
      { kind: 'citation', n: 1, citation: CITATIONS[0] },
      { kind: 'text', text: ' as specified.' },
    ])
  })

  it('resolves multiple markers, including at the very start and end', () => {
    expect(segmentAnswer('[S2]middle[S1]', CITATIONS)).toEqual([
      { kind: 'citation', n: 2, citation: CITATIONS[1] },
      { kind: 'text', text: 'middle' },
      { kind: 'citation', n: 1, citation: CITATIONS[0] },
    ])
  })

  it('repeated markers each resolve to the same citation', () => {
    const segments = segmentAnswer('First [S1], later [S1] again.', CITATIONS)
    const cites = segments.filter((s) => s.kind === 'citation')
    expect(cites).toHaveLength(2)
    expect(cites[0]).toEqual({ kind: 'citation', n: 1, citation: CITATIONS[0] })
    expect(cites[1]).toEqual({ kind: 'citation', n: 1, citation: CITATIONS[0] })
  })

  it('out-of-order references are fine — n indexes the array, not prose order', () => {
    expect(segmentAnswer('[S3] before [S1]', CITATIONS)).toEqual([
      { kind: 'citation', n: 3, citation: CITATIONS[2] },
      { kind: 'text', text: ' before ' },
      { kind: 'citation', n: 1, citation: CITATIONS[0] },
    ])
  })

  it('adjacent markers produce adjacent citation segments with no text between', () => {
    expect(segmentAnswer('Both agree [S1][S2].', CITATIONS)).toEqual([
      { kind: 'text', text: 'Both agree ' },
      { kind: 'citation', n: 1, citation: CITATIONS[0] },
      { kind: 'citation', n: 2, citation: CITATIONS[1] },
      { kind: 'text', text: '.' },
    ])
  })

  it('a marker with no matching citation stays verbatim inside the text', () => {
    // The server strips fabricated markers; this layer still refuses to
    // mint a dead link if one slips through.
    expect(segmentAnswer('Known [S1] but fabricated [S7].', CITATIONS)).toEqual([
      { kind: 'text', text: 'Known ' },
      { kind: 'citation', n: 1, citation: CITATIONS[0] },
      { kind: 'text', text: ' but fabricated [S7].' },
    ])
  })

  it('S0, absurdly wide numbers, and lookalikes are all plain text', () => {
    for (const text of ['zero [S0] based?', 'wide [S99999999999999999999]', 'lower [s1]', 'spaced [S 1]', 'bare S1']) {
      expect(segmentAnswer(text, CITATIONS)).toEqual([{ kind: 'text', text }])
    }
  })

  it('no citations at all: every marker is plain text', () => {
    expect(segmentAnswer('Answer [S1].', [])).toEqual([{ kind: 'text', text: 'Answer [S1].' }])
  })

  it('preserves the full answer byte-for-byte across segments', () => {
    const answer = 'A [S1] b\n\nnew paragraph [S2][S1] end [S9].'
    const segments = segmentAnswer(answer, CITATIONS)
    const rebuilt = segments.map((s) => (s.kind === 'text' ? s.text : `[S${s.n}]`)).join('')
    expect(rebuilt).toBe(answer)
  })
})

describe('citationHref', () => {
  it('builds the same deep link a search hit uses: /pages/{id}#{anchorId}', () => {
    expect(citationHref(c(2))).toBe('/pages/page-2#anchor-2')
  })
})
