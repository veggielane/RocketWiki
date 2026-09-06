import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { ClassificationBanner } from '../ClassificationBanner'

/**
 * The ICDS classification banner's two variants (design.md §21). The screen's
 * banner is placed by the shell (shellClassificationBanner.test.tsx covers
 * where); this pins what each variant is, so a change to one cannot quietly
 * become a change to the other.
 */
describe('ClassificationBanner', () => {
  it('the screen’s banner is a landmark region named for its scope, with a print-only head copy', () => {
    render(<ClassificationBanner label="UK SECRET UK/US EYES ONLY" level="SECRET" scopeLabel="Protective marking for this page" />)
    const region = screen.getByRole('region', { name: 'Protective marking for this page' })
    expect(region.getAttribute('data-classification-banner')).toBe('foot')
    // Lead-in for a screen reader, then the server's label byte-for-byte (§21.4).
    expect(region.textContent).toBe('Protective marking for this page: UK SECRET UK/US EYES ONLY')
    const head = document.querySelector('[data-classification-banner="print-head"]')!
    expect(head.getAttribute('aria-hidden')).toBe('true')
    expect(head.textContent).toBe('UK SECRET UK/US EYES ONLY')
  })

  it('the inline variant is a plain block in the flow: no landmark, no print copy', () => {
    render(<ClassificationBanner inline label="UK OFFICIAL" level="OFFICIAL" scopeLabel="Protective marking for these search results" />)
    // Two landmarks sharing a name would be an axe `landmark-unique` failure
    // beside the screen's banner, so an inline marking is not one.
    expect(screen.queryByRole('region')).toBeNull()
    expect(document.querySelector('[data-classification-banner="inline"]')).not.toBeNull()
    expect(document.querySelector('[data-classification-banner="print-head"]')).toBeNull()
    expect(document.body.textContent).toContain('Protective marking for these search results: UK OFFICIAL')
  })

  it('renders a prefix-less label without inventing one', () => {
    render(<ClassificationBanner label="TOP SECRET" level="TOP_SECRET" scopeLabel="Protective marking for this page" />)
    expect(screen.getByRole('region').textContent).toBe('Protective marking for this page: TOP SECRET')
  })
})
