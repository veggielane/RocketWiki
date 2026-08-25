import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MarkingBanner } from '../MarkingBanner'
import { MarkingLevelBadge } from '../MarkingLevelBadge'
import { CLASSIFICATION_LADDER } from '../clearance'
import { markingTone } from '../markingTone'

/**
 * design.md §21's rendering rules, which are compliance rules rather than
 * cosmetic ones: exactly one formatter (the server's), and colour that never
 * carries meaning on its own (WCAG 1.4.1).
 */
describe('MarkingBanner', () => {
  it('renders the label exactly as given, with nothing added or transformed', () => {
    render(<MarkingBanner label="UK SECRET [UK/US EYES ONLY]" level="SECRET" placement="head" />)
    // Not a regex and not a substring match: byte-for-byte is the point (§21.4).
    expect(screen.getByText('UK SECRET [UK/US EYES ONLY]').textContent).toContain('UK SECRET [UK/US EYES ONLY]')
  })

  it('renders a prefix-less label without inventing one', () => {
    render(<MarkingBanner label="TOP SECRET" level="TOP_SECRET" placement="head" />)
    expect(screen.queryByText(/UK/)).toBeNull()
  })

  it('is not an alert — a marking is persistent context, not an event', () => {
    render(<MarkingBanner label="UK OFFICIAL" level="OFFICIAL" placement="head" />)
    expect(screen.queryByRole('alert')).toBeNull()
    expect(screen.queryByRole('status')).toBeNull()
  })

  it('leads the two placements differently for a screen reader, so the repeat reads as a repeat', () => {
    const { container: head } = render(<MarkingBanner label="UK OFFICIAL" level="OFFICIAL" placement="head" />)
    const { container: foot } = render(<MarkingBanner label="UK OFFICIAL" level="OFFICIAL" placement="foot" />)
    expect(head.textContent).toContain('Protective marking: UK OFFICIAL')
    expect(foot.textContent).toContain('Protective marking, repeated at the foot of the page: UK OFFICIAL')
  })

  it('omits the lead-in inside the marking control, where the heading already says it', () => {
    const { container } = render(<MarkingBanner label="UK OFFICIAL" level="OFFICIAL" placement="section" />)
    expect(container.textContent).toBe('UK OFFICIAL')
  })
})

describe('MarkingLevelBadge', () => {
  it('spells the level as its wire name, the one spelling the SPA does not own (§21.1)', () => {
    render(<MarkingLevelBadge level="OFFICIAL_SENSITIVE" />)
    expect(screen.getByText('OFFICIAL_SENSITIVE')).toBeTruthy()
  })

  it('names itself for a screen reader rather than leaving a bare token in a list row', () => {
    const { container } = render(<MarkingLevelBadge level="SECRET" />)
    expect(container.textContent).toBe('Classification: SECRET')
  })
})

describe('markingTone — colour is an accent, never the signal (WCAG 1.4.1)', () => {
  it('gives every level in the ladder a tone in both themes', () => {
    for (const level of CLASSIFICATION_LADDER) {
      for (const mode of ['light', 'dark'] as const) {
        const tone = markingTone(level, mode)
        expect(tone.bg).toMatch(/^#[0-9a-f]{6}$/)
        expect(tone.fg).toMatch(/^#[0-9a-f]{6}$/)
      }
    }
  })

  it('uses opaque colours only, so the CI contrast check has real pixels to measure', () => {
    for (const level of CLASSIFICATION_LADDER) {
      for (const mode of ['light', 'dark'] as const) {
        const tone = markingTone(level, mode)
        expect(`${tone.bg}${tone.fg}${tone.border}`).not.toMatch(/rgba|hsla|transparent/)
      }
    }
  })

  it('falls back to the loudest tone for a level it cannot place (§21.3s direction)', () => {
    for (const mode of ['light', 'dark'] as const) {
      expect(markingTone('COSMIC' as never, mode)).toEqual(markingTone('TOP_SECRET', mode))
    }
  })

  it('meets 4.5:1 for every level in both themes', () => {
    // Measured here rather than only in the browser tier so a tone change that
    // breaks contrast fails at the unit layer, where the cause is obvious.
    const channel = (c: number) => (c / 255 <= 0.03928 ? c / 255 / 12.92 : Math.pow((c / 255 + 0.055) / 1.055, 2.4))
    const luminance = (hex: string) => {
      const [r, g, b] = [1, 3, 5].map((i) => channel(parseInt(hex.slice(i, i + 2), 16)))
      return 0.2126 * r + 0.7152 * g + 0.0722 * b
    }
    const ratio = (a: string, b: string) => {
      const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x)
      return (hi + 0.05) / (lo + 0.05)
    }
    for (const level of CLASSIFICATION_LADDER) {
      for (const mode of ['light', 'dark'] as const) {
        const tone = markingTone(level, mode)
        expect(ratio(tone.fg, tone.bg)).toBeGreaterThanOrEqual(4.5)
      }
    }
  })
})
