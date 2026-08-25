import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { AggregateMarkingBanner, type AggregateMarkingPlacement } from '../AggregateMarkingBanner'

/**
 * design.md §21.13's two hard rules for the compilation label, both of which
 * are compliance properties rather than cosmetic ones:
 *
 *  1. The server's `label` is rendered verbatim and the client composes
 *     nothing — an aggregate caveat is a CONJUNCTION of distinct source sets
 *     that the per-page shape cannot express, so any assembly here would
 *     either widen it (union) or erase it (intersection).
 *  2. Null means "no sources fed this", and the honest rendering of that is
 *     nothing at all.
 */

const PLACEMENTS: AggregateMarkingPlacement[] = ['answer-head', 'answer-foot', 'results']

describe("AggregateMarkingBanner — the label is the server's, byte for byte", () => {
  it('renders a conjunctive multi-caveat label exactly as given', () => {
    render(
      <AggregateMarkingBanner
        marking={{ level: 'SECRET', label: 'UK SECRET [GB EYES ONLY] [US EYES ONLY]' }}
        placement="answer-head"
      />,
    )
    // Not a substring probe and not a regex: this exact string, because it is
    // the one an SPA-side formatter would get wrong.
    expect(screen.getByText('UK SECRET [GB EYES ONLY] [US EYES ONLY]')).toBeInTheDocument()
  })

  it('renders a prefix-less label without inventing one (unanimity-only prefix, §21.13)', () => {
    // Sources that disagreed on prefix drop to the bare level. The SPA must
    // not put "UK" back on.
    render(<AggregateMarkingBanner marking={{ level: 'SECRET', label: 'SECRET' }} placement="results" />)
    expect(screen.queryByText(/UK|US|GB/)).toBeNull()
  })

  it('uses level for tone only — nothing readable is derived from it', () => {
    const { container } = render(
      <AggregateMarkingBanner
        marking={{ level: 'OFFICIAL_SENSITIVE', label: 'UK OFFICIAL-SENSITIVE' }}
        placement="results"
      />,
    )
    // The wire name is a machine identifier and must never reach a reader.
    expect(container.textContent).not.toContain('OFFICIAL_SENSITIVE')
  })
})

describe('AggregateMarkingBanner — the nullable case', () => {
  it.each(PLACEMENTS)('renders nothing at all for a null aggregate (%s)', (placement) => {
    const { container } = render(<AggregateMarkingBanner marking={null} placement={placement} />)
    // Not "UNMARKED", not OFFICIAL, not a placeholder, not an empty styled
    // box: an absent marking is a fact, and substituting one would be worse
    // than showing none.
    expect(container).toBeEmptyDOMElement()
  })

  it('treats an absent field the same as an explicit null', () => {
    const { container } = render(<AggregateMarkingBanner marking={undefined} placement="results" />)
    expect(container).toBeEmptyDOMElement()
  })
})

describe('AggregateMarkingBanner — what the label covers', () => {
  const marking = { level: 'SECRET' as const, label: 'UK SECRET' }

  it('tells a search reader the label spans the whole result set, not the visible rows', () => {
    render(<AggregateMarkingBanner marking={marking} placement="results" />)
    expect(screen.getByText('Covers every result for this search, including any not shown here.')).toBeInTheDocument()
  })

  it('tells an answer reader the label spans uncited context too', () => {
    render(<AggregateMarkingBanner marking={marking} placement="answer-head" />)
    expect(screen.getByText('Covers every page this answer drew on, including any not cited.')).toBeInTheDocument()
  })

  it('does not repeat the scope note on the foot marking', () => {
    const { container } = render(<AggregateMarkingBanner marking={marking} placement="answer-foot" />)
    expect(container.textContent).not.toContain('Covers every')
    expect(container.textContent).toContain('UK SECRET')
  })

  it('says only what the label covers — never what a classification permits', () => {
    // §21.13 guardrail: the aggregate states what the text IS. Telling a
    // reader what they may do with it is a judgement the wiki does not make.
    for (const placement of PLACEMENTS) {
      const { container } = render(<AggregateMarkingBanner marking={marking} placement={placement} />)
      expect(container.textContent).not.toMatch(/safe|share|permit|allowed|cleared to/i)
    }
  })
})

describe('AggregateMarkingBanner — announced as what it marks', () => {
  it('names the answer, the repeat and the result set distinctly for a screen reader', () => {
    const marking = { level: 'SECRET' as const, label: 'UK SECRET' }
    const text = (placement: AggregateMarkingPlacement) =>
      render(<AggregateMarkingBanner marking={marking} placement={placement} />).container.textContent ?? ''

    // "this page's marking" would attach the label to the wrong thing for the
    // one reader who cannot see where the banner sits.
    expect(text('answer-head')).toContain('Protective marking for this answer: UK SECRET')
    expect(text('answer-foot')).toContain('Protective marking, repeated at the end of this answer: UK SECRET')
    expect(text('results')).toContain('Protective marking for these search results: UK SECRET')
  })

  it('is not an alert — a marking is persistent context, not an event', () => {
    render(<AggregateMarkingBanner marking={{ level: 'TOP_SECRET', label: 'TOP SECRET' }} placement="answer-head" />)
    expect(screen.queryByRole('alert')).toBeNull()
    expect(screen.queryByRole('status')).toBeNull()
  })
})
