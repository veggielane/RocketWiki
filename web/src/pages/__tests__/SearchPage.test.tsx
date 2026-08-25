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

function renderSearch() {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SearchFacets') return { spaces: [{ key: 'ENG', name: 'Engineering' }], labels: [] }
    if (name === 'SearchPages')
      return {
        search: {
          totalCount: 2,
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
