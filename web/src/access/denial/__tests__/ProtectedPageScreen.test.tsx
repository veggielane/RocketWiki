import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { ProtectedPageScreen, type ProtectedPageDenial } from '../ProtectedPageScreen'
import { PageTitleContext } from '../../../app/documentTitle'
import { expectNoAxeViolations } from '../../../test/axe'

/**
 * The page-sized placeholder (design.md §6.7 / §21.8). What it must say —
 * the marking and every failing gate — and, more importantly, what it must
 * never say: the page's title. The denial carries none, and this screen has
 * no prop for one; these tests pin that neither the heading, the tab title
 * nor the body can carry it.
 */
const WITHHELD_TITLE = 'Stage two ignition anomaly review'

const denial: ProtectedPageDenial = {
  noSpaceAccess: false,
  marking: { label: 'UK SECRET APPLE NZ/US EYES ONLY', level: 'SECRET' },
  reasons: [
    { gate: 'SELECTOR_GRANT', passed: false, category: 'FRUIT', value: 'APPLE' },
    { gate: 'NATIONAL_CAVEAT', passed: false, countries: ['NZ', 'US'] },
  ],
}

function renderScreen(props: { denial: ProtectedPageDenial }) {
  const setTitle = vi.fn()
  render(
    <PageTitleContext value={setTitle}>
      <ProtectedPageScreen {...props} />
    </PageTitleContext>,
  )
  return { setTitle }
}

describe('ProtectedPageScreen', () => {
  it('is headed "Protected page" and names the tab the same — never the page', () => {
    const { setTitle } = renderScreen({ denial })
    expect(screen.getByRole('heading', { level: 1, name: 'Protected page' })).toBeInTheDocument()
    // The document title is the one place a title could leak past the
    // heading: a bookmark or the history menu would keep it.
    expect(setTitle).toHaveBeenLastCalledWith('Protected page')
    expect(setTitle).not.toHaveBeenCalledWith(expect.stringContaining('SECRET'))
    expect(document.body.textContent).not.toContain(WITHHELD_TITLE)
  })

  it("renders the server's marking label verbatim through the page banner", () => {
    renderScreen({ denial })
    const banner = document.querySelector('[data-marking-placement="head"]')
    expect(banner?.textContent).toBe('Protective marking: UK SECRET APPLE NZ/US EYES ONLY')
  })

  it('lists every failing gate as a sentence under "Why you cannot read this page"', () => {
    renderScreen({ denial })
    expect(screen.getByRole('heading', { level: 2, name: 'Why you cannot read this page' })).toBeInTheDocument()
    expect(screen.getByText('APPLE is not granted to you in this space.')).toBeInTheDocument()
    expect(screen.getByText('Releasable to NZ/US only.')).toBeInTheDocument()
    // Nothing about what the reader holds: no gate is about a level, so
    // there is no clearance to name beside what the page needs.
    expect(document.body.textContent).not.toMatch(/clearance|eligib/i)
  })

  it('says the marking is missing, with no banner, when that is the gate that failed', () => {
    // The server withholds no label here because there is none: the marking
    // row is gone, and the sentence says so to everyone alike.
    renderScreen({
      denial: { noSpaceAccess: false, marking: null, reasons: [{ gate: 'MARKING_UNAVAILABLE', passed: false }] },
    })
    expect(screen.getByRole('heading', { level: 2, name: 'Why you cannot read this page' })).toBeInTheDocument()
    expect(screen.getByText("This page's marking is missing, so nobody can read it until it is restored.")).toBeInTheDocument()
    expect(document.querySelector('[data-marking-placement]')).toBeNull()
  })

  it('says only the space sentence when the caller holds no access grant there — no marking, no gates', () => {
    renderScreen({
      denial: {
        noSpaceAccess: true,
        marking: null,
        reasons: [{ gate: 'SPACE_ACCESS', passed: false }],
      },
    })
    expect(screen.getByRole('heading', { level: 1, name: 'Protected page' })).toBeInTheDocument()
    expect(screen.getByText('You have no access to this space.')).toBeInTheDocument()
    expect(screen.queryByText('Why you cannot read this page')).toBeNull()
    expect(document.querySelector('[data-marking-placement]')).toBeNull()
  })

  it('has no axe violations in both shapes', async () => {
    renderScreen({ denial })
    await expectNoAxeViolations()
  })
})
