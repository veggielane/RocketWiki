import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { PageHistoryPage } from '../PageHistoryPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

const ada = { id: 'u1', displayName: 'Ada Lovelace', hasAvatar: false }
const grace = { id: 'u2', displayName: 'Grace Hopper', hasAvatar: false }

const revision = (n: number, content: string, extra: Record<string, unknown> = {}) => ({
  id: `r${n}`,
  revisionNumber: n,
  title: 'Runbook',
  content,
  editSummary: null,
  createdAtUtc: `2026-08-${String(10 + n).padStart(2, '0')}T09:00:00Z`,
  author: ada,
  contributors: [],
  ...extra,
})

const REVISIONS = [
  revision(1, 'The quick brown fox.\n'),
  revision(2, 'The slow brown fox.\n', { editSummary: 'Slowed the fox', author: grace }),
  revision(3, 'The slow brown dog.\n', { contributors: [grace, ada] }),
]

function renderHistory({ revisions = REVISIONS as unknown[], page = {} as Record<string, unknown> } = {}) {
  const mock = createMockUrqlClient((name) =>
    name === 'PageHistory'
      ? { page: { id: 'page-1', title: 'Runbook', canEdit: true, revisions, ...page } }
      : undefined,
  )
  render(
    <MemoryRouter initialEntries={['/pages/page-1/history']}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route path="/pages/:pageId/history" element={<PageHistoryPage />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

describe('PageHistoryPage', () => {
  it('lists every revision, newest first', async () => {
    // The question people arrive with is "what changed recently".
    renderHistory()
    const table = await screen.findByRole('table', { name: 'Revisions' })
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows).toHaveLength(3)
    expect(rows[0]!.textContent).toContain('3')
    expect(rows[2]!.textContent).toContain('1')
  })

  it('names who saved each revision, each name a link to their profile', async () => {
    renderHistory()
    const table = await screen.findByRole('table', { name: 'Revisions' })
    expect(within(table).getAllByText('Ada Lovelace').length).toBeGreaterThan(0)
    // A byline is a link (users/UserLink.tsx): the name goes somewhere.
    expect(within(table).getAllByRole('link', { name: 'Ada Lovelace' })[0]).toHaveAttribute('href', '/users/u1')
    expect(within(table).getAllByRole('link', { name: 'Grace Hopper' })[0]).toHaveAttribute('href', '/users/u2')
  })

  it('credits who typed as well, when a revision was co-edited', async () => {
    // A co-edited revision carries other people's live edits; crediting only the
    // person who pressed save would be wrong about it. The credit reads "with
    // Grace Hopper", the name a profile link like the author's.
    renderHistory()
    const table = await screen.findByRole('table', { name: 'Revisions' })
    const credit = within(table).getByText(/^with/)
    expect(credit).toHaveTextContent('with Grace Hopper')
    expect(within(credit).getByRole('link', { name: 'Grace Hopper' })).toHaveAttribute('href', '/users/u2')
  })

  it('shows an edit summary where there is one, and says so where there is not', async () => {
    renderHistory()
    const table = await screen.findByRole('table', { name: 'Revisions' })
    expect(within(table).getByText('Slowed the fox')).toBeInTheDocument()
    expect(within(table).getAllByText('—').length).toBe(2)
  })

  it('defaults to comparing the two most recent, which is what people want', async () => {
    renderHistory()
    await screen.findByRole('table', { name: 'Revisions' })
    expect(screen.getByRole('radio', { name: 'Compare to revision 3' })).toBeChecked()
    expect(screen.getByRole('radio', { name: 'Compare from revision 2' })).toBeChecked()
    // And the diff of those two is on screen without a click.
    expect(screen.getByRole('region', { name: /revision 2 and revision 3/ })).toBeInTheDocument()
  })

  it('diffs the two revisions you pick', async () => {
    renderHistory()
    await screen.findByRole('table', { name: 'Revisions' })
    fireEvent.click(screen.getByRole('radio', { name: 'Compare from revision 1' }))

    const diff = await screen.findByRole('region', { name: /revision 1 and revision 3/ })
    // Both edits between 1 and 3 show: quick→slow and fox→dog.
    expect(diff.textContent).toContain('quick')
    expect(diff.textContent).toContain('dog')
  })

  it('reads older-to-newer whichever way round they were picked', async () => {
    // Otherwise selecting bottom-up would invert what counts as an addition, and
    // the same pair of revisions would tell two different stories.
    renderHistory()
    await screen.findByRole('table', { name: 'Revisions' })
    fireEvent.click(screen.getByRole('radio', { name: 'Compare from revision 3' }))
    fireEvent.click(screen.getByRole('radio', { name: 'Compare to revision 1' }))

    const diff = await screen.findByRole('region', { name: /revision 3 and revision 1/ })
    // Revision 1's text is the BEFORE, so the newer word is the addition.
    const added = within(diff).getAllByText('dog', { exact: false })
    expect(added.length).toBeGreaterThan(0)
  })

  it('says so when there is only one revision to look at', async () => {
    renderHistory({ revisions: [revision(1, 'Only one.\n')] })
    expect(await screen.findByText(/only one revision/)).toBeInTheDocument()
  })

  it('shows the same not-found state for a missing page and one you cannot view', async () => {
    // §6.7 — the history screen does not sharpen a distinction the server blurred.
    const mock = createMockUrqlClient((name) => (name === 'PageHistory' ? { page: null } : undefined))
    render(
      <MemoryRouter initialEntries={['/pages/page-1/history']}>
        <UrqlProvider value={mock.client}>
          <Routes>
            <Route path="/pages/:pageId/history" element={<PageHistoryPage />} />
          </Routes>
        </UrqlProvider>
      </MemoryRouter>,
    )
    expect(await screen.findByText("Couldn't load this page.")).toBeInTheDocument()
  })

  it('has no axe violations', async () => {
    renderHistory()
    await screen.findByRole('table', { name: 'Revisions' })
    await expectNoAxeViolations()
  })
})
