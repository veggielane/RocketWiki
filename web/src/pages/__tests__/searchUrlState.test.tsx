import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
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
