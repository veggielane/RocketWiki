import { describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { SearchPage } from '../SearchPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'

/**
 * The search screen's state has to live in the URL, and the query field has to
 * survive somebody else changing it.
 *
 * The bug: two effects compared `debouncedDraft` against `query` in both
 * directions. Submitting a new query from the header while already on `/search`
 * ran effect A first with a STALE debounced value, which rewrote the URL back to
 * the old query over the navigation that had just happened — and effect B then
 * pulled the field back too, cancelling the pending timer. The typed query was
 * silently discarded, and the same trace applied to Back/Forward between two
 * searches.
 *
 * Nothing caught it because the existing test only ever mounted once at a fixed
 * `?q=`, so no second navigation ever happened.
 */

function Where() {
  const { search } = useLocation()
  return <span data-testid="search-params">{search}</span>
}

/** A real in-router navigation — what the header's search box does. */
function GoTo({ to, label }: { to: string; label: string }) {
  const navigate = useNavigate()
  return (
    <button type="button" onClick={() => navigate(to)}>
      {label}
    </button>
  )
}

function renderSearch(initialEntry: string) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SearchFacets')
      return {
        spaces: [
          { key: 'ENG', name: 'Engineering' },
          { key: 'OPS', name: 'Operations' },
        ],
        labels: ['runbook', 'anomaly'],
      }
    if (name === 'SearchPages')
      return {
        search: {
          aggregateMarking: null,
          totalCount: 0,
          pageInfo: { hasNextPage: false, endCursor: null },
          edges: [],
        },
      }
    return undefined
  })

  render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route
            path="/search"
            element={
              <>
                <SearchPage />
                <Where />
                <GoTo to="/search?q=beta" label="header submit beta" />
              </>
            }
          />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

const queryField = () => screen.getByLabelText('Query') as HTMLInputElement
const currentParams = () => screen.getByTestId('search-params').textContent

describe('search query and the URL', () => {
  it('adopts a query submitted from elsewhere instead of rewriting it back', async () => {
    // The headline defect: this used to settle back on "alpha".
    renderSearch('/search?q=alpha')
    await waitFor(() => expect(queryField().value).toBe('alpha'))

    fireEvent.click(screen.getByRole('button', { name: 'header submit beta' }))

    await waitFor(() => expect(queryField().value).toBe('beta'))
    expect(currentParams()).toContain('q=beta')
    // And it stays there — the stale debounce must not fire later and undo it.
    await new Promise((r) => setTimeout(r, 400))
    expect(currentParams()).toContain('q=beta')
    expect(queryField().value).toBe('beta')
  })

  it('still writes a typed query through to the URL', async () => {
    renderSearch('/search?q=alpha')
    await waitFor(() => expect(queryField().value).toBe('alpha'))

    fireEvent.change(queryField(), { target: { value: 'ignition' } })
    await waitFor(() => expect(currentParams()).toContain('q=ignition'), { timeout: 2000 })
  })
})

describe('search filters live in the URL', () => {
  it('reads the space and labels from the query string', async () => {
    renderSearch('/search?q=alpha&space=ENG&label=runbook&label=anomaly')
    await waitFor(() => expect(queryField().value).toBe('alpha'))

    expect(screen.getByLabelText('Space')).toHaveValue('Engineering')
    // Multi-select labels render as chips.
    expect(screen.getByRole('button', { name: 'runbook' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'anomaly' })).toBeInTheDocument()
  })

  it('writes a chosen space into the URL, so the search can be shared or returned to', async () => {
    renderSearch('/search?q=alpha')
    await waitFor(() => expect(queryField().value).toBe('alpha'))

    fireEvent.mouseDown(screen.getByLabelText('Space'))
    fireEvent.click(await screen.findByRole('option', { name: 'Operations' }))

    await waitFor(() => expect(currentParams()).toContain('space=OPS'))
    // The query is not lost when a filter changes.
    expect(currentParams()).toContain('q=alpha')
  })

  it('offers a way out of a filter that eliminated everything', async () => {
    renderSearch('/search?q=alpha&space=ENG')
    await waitFor(() => expect(queryField().value).toBe('alpha'))

    // The empty state names the filters rather than blaming the spelling.
    expect(screen.getByText(/with the current filters/)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }))
    await waitFor(() => expect(currentParams()).not.toContain('space='))
    expect(currentParams()).toContain('q=alpha')
  })

  it('offers no clear-filters control when nothing is filtered', async () => {
    renderSearch('/search?q=alpha')
    await waitFor(() => expect(queryField().value).toBe('alpha'))
    expect(screen.queryByRole('button', { name: 'Clear filters' })).not.toBeInTheDocument()
  })
})

/**
 * How far down the results you are, in the URL.
 *
 * Pagination was the one part of this screen that could not live there. It was
 * a cursor plus an accumulated `edges` array in component state, and a cursor
 * names a boundary rather than a run — so a restored link could only be rebuilt
 * by replaying every request that built it, which the SPA had no way to do.
 * `search(first:)` fetches the run in one request, so the count goes in the URL
 * and the accumulation disappears.
 */

/** A results page N rows long. */
function pageOf(count: number, { hasNextPage = true, totalCount = 100 } = {}) {
  return {
    search: {
      aggregateMarking: null,
      totalCount,
      pageInfo: { hasNextPage },
      edges: Array.from({ length: count }, (_, i) => ({
        cursor: `c${i}`,
        node: {
          snippet: `snippet ${i}`,
          headingPath: [],
          anchorId: `a${i}`,
          page: {
            id: `p${i}`,
            title: `Result ${i}`,
            spaceKey: 'ENG',
            marking: {
              level: 'OFFICIAL',
              levelName: 'OFFICIAL',
              eyesOnly: [],
              prefix: 'UK',
              label: 'UK OFFICIAL',
            },
          },
        },
      })),
    },
  }
}

function renderResults(initialEntry: string, { totalCount = 100, hasNextPage = true } = {}) {
  const mock = createMockUrqlClient((name, op) => {
    if (name === 'SearchFacets') return { spaces: [{ key: 'ENG', name: 'Engineering' }], labels: ['runbook'] }
    if (name === 'SearchPages') return pageOf(Number(op.variables?.first ?? 20), { hasNextPage, totalCount })
    return undefined
  })
  render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route
            path="/search"
            element={
              <>
                <SearchPage />
                <Where />
              </>
            }
          />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

/** How many rows the last SearchPages operation asked for. */
const lastFirst = (mock: { operations: { name: string; variables: Record<string, unknown> }[] }) =>
  [...mock.operations].reverse().find((o) => o.name === 'SearchPages')?.variables.first

/** Every size this render has asked the server for, in order. */
const allFirsts = (mock: { operations: { name: string; variables: Record<string, unknown> }[] }) =>
  mock.operations.filter((o) => o.name === 'SearchPages').map((o) => o.variables.first)

describe('how much of the result set is on screen lives in the URL', () => {
  it('asks for one page by default', () => {
    expect(lastFirst(renderResults('/search?q=alpha'))).toBe(20)
  })

  it('records "show more" in the address and asks for the longer run', async () => {
    const mock = renderResults('/search?q=alpha')

    fireEvent.click(screen.getByRole('button', { name: 'Show 20 more' }))

    await waitFor(() => expect(currentParams()).toContain('show=40'))
    await waitFor(() => expect(lastFirst(mock)).toBe(40))
    expect(await screen.findByText('Result 39')).toBeInTheDocument()
  })

  it('rebuilds a shared link’s whole list in ONE request, which is what cursors could not do', () => {
    const mock = renderResults('/search?q=alpha&show=60')

    // The SIZES asked for, deduplicated — a repeat of the same request is
    // urql re-executing, but a walk of 20 → 40 → 60 would be the old cursor
    // replay this replaces.
    expect([...new Set(allFirsts(mock))]).toEqual([60])
    expect(screen.getByText('Result 59')).toBeInTheDocument()
  })

  it('clamps a stale or hand-edited size instead of refusing it', () => {
    expect(lastFirst(renderResults('/search?q=alpha&show=5000'))).toBe(100)
    cleanup()
    expect(lastFirst(renderResults('/search?q=alpha&show=-3'))).toBe(20)
    cleanup()
    expect(lastFirst(renderResults('/search?q=alpha&show=banana'))).toBe(20)
  })

  it('starts over at one page when a filter changes', async () => {
    renderResults('/search?q=alpha&show=60')

    fireEvent.mouseDown(screen.getByLabelText('Space'))
    fireEvent.click(await screen.findByRole('option', { name: 'Engineering' }))

    await waitFor(() => expect(currentParams()).toContain('space=ENG'))
    expect(currentParams()).not.toContain('show=')
  })

  it('starts over at one page when the query changes', async () => {
    renderResults('/search?q=alpha&show=60')

    fireEvent.change(queryField(), { target: { value: 'ignition' } })

    await waitFor(() => expect(currentParams()).toContain('q=ignition'), { timeout: 2000 })
    expect(currentParams()).not.toContain('show=')
  })
})

describe('the saturating result total is never printed as an exact one', () => {
  it('says "at least 100" at the cap', () => {
    // §6.7: the server stops counting at 100 rather than evaluate canView over
    // the whole database, so 100 means "100 or more". Printing it as a total
    // would be the screen inventing precision the server refused to give.
    renderResults('/search?q=alpha', { totalCount: 100 })
    expect(screen.getByText(/At least 100 results/)).toBeInTheDocument()
  })

  it('still gives an exact count below the cap', () => {
    renderResults('/search?q=alpha', { totalCount: 7, hasNextPage: false })
    expect(screen.getByText(/^7 results/)).toBeInTheDocument()
  })

  it('drives "show more" from hasNextPage, not from the total', () => {
    // At exactly the cap with nothing behind it, a `shown < totalCount`
    // comparison would offer a next page forever.
    renderResults('/search?q=alpha', { totalCount: 100, hasNextPage: false })
    expect(screen.queryByRole('button', { name: /Show \d+ more/ })).not.toBeInTheDocument()
  })
})
