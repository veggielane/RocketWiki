import { beforeEach, describe, expect, it } from 'vitest'
import { act, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { AskWikiEntryButton } from '../AskWikiEntryButton'
import { AskWikiSearchNudge } from '../AskWikiSearchNudge'
import { markAskWikiNotConfigured, resetAskWikiAvailability } from '../askAvailability'
import { SearchPage } from '../../pages/SearchPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'

/**
 * The two entry points to /ask, and their session-level collapse: once any
 * ask has answered NOT_CONFIGURED (askAvailability.ts — the attempt is the
 * probe, there is no assistant status query), both must vanish live, not
 * on next mount.
 */

beforeEach(() => {
  resetAskWikiAvailability()
})

describe('AskWikiEntryButton (app bar, next to search)', () => {
  it('links to /ask with an accessible name, and vanishes live when the session learns NOT_CONFIGURED', () => {
    render(
      <MemoryRouter>
        <AskWikiEntryButton />
      </MemoryRouter>,
    )
    expect(screen.getByRole('link', { name: 'Ask the wiki' })).toHaveAttribute('href', '/ask')
    act(() => markAskWikiNotConfigured())
    expect(screen.queryByRole('link', { name: 'Ask the wiki' })).not.toBeInTheDocument()
  })
})

describe('AskWikiSearchNudge ("Can\'t find it?")', () => {
  it('prefills /ask with the URL-encoded query', () => {
    render(
      <MemoryRouter>
        <AskWikiSearchNudge query="seal material? Mk2" />
      </MemoryRouter>,
    )
    expect(screen.getByRole('link', { name: 'Ask the wiki' })).toHaveAttribute(
      'href',
      '/ask?q=seal%20material%3F%20Mk2',
    )
  })

  it('renders nothing once the session knows the assistant is absent', () => {
    markAskWikiNotConfigured()
    const { container } = render(
      <MemoryRouter>
        <AskWikiSearchNudge query="anything" />
      </MemoryRouter>,
    )
    expect(container).toBeEmptyDOMElement()
  })

  it('appears on the search page under a completed search', async () => {
    const mock = createMockUrqlClient((name) => {
      if (name === 'SearchFacets') return { spaces: [], labels: [] }
      if (name === 'SearchPages')
        return { search: { totalCount: 0, pageInfo: { hasNextPage: false, endCursor: null }, edges: [] } }
      return undefined
    })
    render(
      <UrqlProvider value={mock.client}>
        <MemoryRouter initialEntries={['/search?q=flux']}>
          <SearchPage />
        </MemoryRouter>
      </UrqlProvider>,
    )
    expect(await screen.findByText('No results for "flux".')).toBeInTheDocument()
    expect(screen.getByText(/Can't find it\?/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Ask the wiki' })).toHaveAttribute('href', '/ask?q=flux')
  })
})
