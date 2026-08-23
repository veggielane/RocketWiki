import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'

import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { PageEditPage } from '../PageEditPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import type { MutationErrorFragment } from '../../graphql/generated/graphql'

const page = {
  id: 'page-1',
  spaceId: 'space-1',
  spaceKey: 'ENG',
  title: 'Runbook',
  slug: 'runbook',
  content: 'Hello world.\n',
  currentRevisionNumber: 3,
  labels: [],
  parent: null,
  children: [],
  comments: [],
  attachments: [],
}

function mutationError(overrides: Partial<MutationErrorFragment>): MutationErrorFragment {
  return {
    kind: 'Validation',
    message: null,
    expectedRevisionNumber: null,
    actualRevisionNumber: null,
    latestTitle: null,
    latestContent: null,
    spaceId: null,
    originInstanceId: null,
    blockedPageCount: null,
    notFoundId: null,
    ...overrides,
  }
}

function renderEditPage(updateError: MutationErrorFragment | null) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'PageById') return { page }
    if (name === 'UpdatePageContent') return { updatePageContent: { page: updateError ? null : page, error: updateError } }
    return undefined
  })

  render(
    <MemoryRouter initialEntries={['/pages/page-1/edit']}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route path="/pages/:pageId/edit" element={<PageEditPage />} />
          <Route path="/pages/:pageId" element={<div>view route</div>} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

/**
 * The two designed failure modes are UX, not exceptions (brief +
 * design.md §8/§12) — asserted here against the REAL flattened error shape
 * (`error.kind` discriminator), which is exactly what the schema
 * reconciliation moved this page onto.
 */
describe('PageEditPage typed mutation errors', () => {
  it('ReadOnlyReplica opens the replica dialog with originInstanceId carried through — never a raw error', async () => {
    renderEditPage(mutationError({ kind: 'ReadOnlyReplica', spaceId: 'space-1', originInstanceId: 'LOW' }))

    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))

    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('read-only')
    // design.md §12: "mirrored from LOW — read-only" needs the origin id.
    expect(dialog).toHaveTextContent('LOW')
  })

  it('StaleRevision opens the merge flow with their revision number, not an exception', async () => {
    renderEditPage(
      mutationError({
        kind: 'StaleRevision',
        expectedRevisionNumber: 3,
        actualRevisionNumber: 7,
        latestContent: 'their newer content',
      }),
    )

    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))

    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Someone else saved changes first')
    expect(dialog).toHaveTextContent('revision 7')
    // The three designed choices are all offered.
    expect(screen.getByRole('button', { name: 'View their changes' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Overwrite anyway' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy my text and cancel' })).toBeInTheDocument()
  })

  it('"Overwrite anyway" re-submits against the revision the error reported', async () => {
    const mock = renderEditPage(
      mutationError({ kind: 'StaleRevision', expectedRevisionNumber: 3, actualRevisionNumber: 7 }),
    )

    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Overwrite anyway' }))

    await waitFor(() => {
      const updates = mock.operations.filter((op) => op.name === 'UpdatePageContent')
      expect(updates).toHaveLength(2)
      const secondInput = updates[1].variables['input'] as { expectedRevisionNumber: number }
      expect(secondInput.expectedRevisionNumber).toBe(7)
    })
  })

  it('a successful save navigates to the page view', async () => {
    renderEditPage(null)

    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))

    expect(await screen.findByText('view route')).toBeInTheDocument()
  })
})
