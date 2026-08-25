import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { SearchPage } from '../SearchPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * design.md §21: results are already clearance-filtered server-side (§21.9's
 * post-filter), so the classification on a row is not a gate — it is the fact
 * a reader scanning a list needs before deciding which hit to open.
 */
const hit = (id: string, title: string, level: string, levelName: string, label: string) => ({
  cursor: id,
  node: {
    snippet: '…matched text from the page body…',
    headingPath: [title],
    anchorId: 'anchor',
    page: { id, title, spaceKey: 'ENG', marking: { level, levelName, eyesOnly: [], prefix: 'UK', label } },
  },
})

/**
 * §21.13's aggregate over the whole permission-filtered hit set. Defaults to
 * null — the honest value for "nothing fed this" — so every pre-existing
 * assertion below still describes a screen with only row badges on it.
 */
type Aggregate = { level: string; label: string } | null

function renderSearch(aggregateMarking: Aggregate = null, totalCount = 2) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SearchFacets') return { spaces: [{ key: 'ENG', name: 'Engineering' }], labels: [] }
    if (name === 'SearchPages')
      return {
        search: {
          aggregateMarking,
          totalCount,
          pageInfo: { hasNextPage: false, endCursor: null },
          edges: [
            // The hyphenated form is the UK Government's written spelling and
            // it comes from the server (§21.1's levelName) — the SPA must not
            // be able to produce it from the OFFICIAL_SENSITIVE wire name.
            hit('page-1', 'Runbook', 'OFFICIAL_SENSITIVE', 'OFFICIAL-SENSITIVE', 'UK OFFICIAL-SENSITIVE'),
            hit('page-2', 'Igniter data', 'SECRET', 'SECRET', 'UK SECRET [UK EYES ONLY]'),
          ],
        },
      }
    if (name === 'GitLabStatus')
      return { gitlabStatus: { configured: false, baseUrl: null, viewerHasToken: false } }
    return undefined
  })
  render(
    <MemoryRouter initialEntries={['/search?q=igniter']}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route path="/search" element={<SearchPage />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

/** The result rows are links; the title alone also appears in each row's breadcrumb. */
const resultRow = async (pageId: string) => {
  const links = await screen.findAllByRole('link')
  return links.find((link) => link.getAttribute('href')?.startsWith(`/pages/${pageId}`))
}

describe('SearchPage protective markings (design.md §21)', () => {
  it('shows each result its classification, as text', async () => {
    renderSearch()
    expect((await resultRow('page-2'))?.textContent).toContain('Classification: SECRET')
  })

  it("uses the server's display spelling, never the wire name (design.md §21.1)", async () => {
    renderSearch()
    const row = await resultRow('page-1')
    expect(row?.textContent).toContain('Classification: OFFICIAL-SENSITIVE')
    // The underscored form is a machine identifier and must never reach a
    // reader: three spellings, one method each, all of them on the server.
    expect(row?.textContent).not.toContain('OFFICIAL_SENSITIVE')
  })

  it('shows the level, not the whole marking — the caveat belongs on the page itself', async () => {
    renderSearch()
    await resultRow('page-2')
    expect(screen.queryByText(/EYES ONLY/)).toBeNull()
  })

  it('has no axe violations with badged results', async () => {
    renderSearch()
    await resultRow('page-2')
    await expectNoAxeViolations()
  })
})

/**
 * design.md §21.13: a result list is a COMPILATION and carries the
 * classification of its most sensitive constituent. The aggregate spans the
 * whole permission-filtered hit set — the same set `totalCount` counts — not
 * the edges currently on screen, which is the property these tests pin.
 */
describe('SearchPage aggregate marking (design.md §21.13)', () => {
  const TOP_SECRET_AGGREGATE = { level: 'TOP_SECRET', label: 'TOP SECRET [GB EYES ONLY] [US EYES ONLY]' }

  it('renders the aggregate label verbatim above the results', async () => {
    renderSearch(TOP_SECRET_AGGREGATE)
    await resultRow('page-1')
    const banner = document.querySelector('[data-aggregate-marking="results"]')
    // Byte-for-byte: the conjunctive caveat is a shape only the server's
    // formatter builds, and any client-side assembly would mangle it.
    expect(banner?.textContent).toContain('TOP SECRET [GB EYES ONLY] [US EYES ONLY]')
  })

  it('out-ranks every row on screen without that being a contradiction', async () => {
    // The staged hits top out at SECRET; the aggregate is TOP SECRET because
    // the hit that earns it is somewhere further down the filtered set.
    renderSearch(TOP_SECRET_AGGREGATE, 96)
    await resultRow('page-2')
    expect(document.querySelector('[data-aggregate-marking="results"]')?.textContent).toContain('TOP SECRET')
    for (const row of ['page-1', 'page-2']) {
      expect((await resultRow(row))?.textContent).not.toContain('TOP SECRET')
    }
  })

  it('says the label covers the whole result set, never only what is visible', async () => {
    renderSearch(TOP_SECRET_AGGREGATE, 96)
    await resultRow('page-1')
    expect(screen.getByText('Covers every result for this search, including any not shown here.')).toBeInTheDocument()
  })

  it('names what it marks for a screen reader — these results, not this page', async () => {
    renderSearch(TOP_SECRET_AGGREGATE)
    await resultRow('page-1')
    expect(document.querySelector('[data-aggregate-marking="results"]')?.textContent).toContain(
      'Protective marking for these search results:',
    )
  })

  it('a null aggregate renders no banner and no substitute (§21.13)', async () => {
    // No sources means no label: not OFFICIAL, not TOP SECRET, not a
    // placeholder. The absence is the honest output.
    renderSearch(null)
    await resultRow('page-1')
    expect(document.querySelector('[data-aggregate-marking]')).toBeNull()
    expect(screen.queryByText(/UNMARKED|Unmarked|Not marked/)).toBeNull()
    expect(screen.queryByText(/Covers every result/)).toBeNull()
  })

  it('has no axe violations with an aggregate banner over badged results', async () => {
    renderSearch(TOP_SECRET_AGGREGATE, 96)
    await resultRow('page-1')
    await expectNoAxeViolations()
  })
})
