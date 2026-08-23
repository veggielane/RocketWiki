import { describe, expect, it, vi } from 'vitest'
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
  canEdit: true,
  canComment: true,
  canManageAccess: false,
  viewerIsWatching: false,
  labels: [],
  labelDetails: [],
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

function renderEditPage(updateError: MutationErrorFragment | null, pageOverrides: Partial<typeof page> = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'PageById') return { page: { ...page, ...pageOverrides } }
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

  it('StaleRevision opens the merge flow with their revision number and the diff as evidence, not an exception', async () => {
    renderEditPage(
      mutationError({
        kind: 'StaleRevision',
        expectedRevisionNumber: 3,
        actualRevisionNumber: 7,
        latestTitle: 'Runbook (revised)',
        latestContent: 'their newer content\n',
      }),
    )

    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))

    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Someone else saved changes first')
    expect(dialog).toHaveTextContent('revision 7')

    // "View their changes" is not a further click away: the diff of the
    // error's latestContent against the draft is the dialog body.
    const diff = screen.getByRole('region', { name: 'Their changes compared with your draft' })
    expect(diff).toHaveTextContent('their newer content')
    expect(diff).toHaveTextContent('Hello world.')
    // Both titles are shown, because theirs changed too.
    expect(dialog).toHaveTextContent('Their title: Runbook (revised)')
    expect(dialog).toHaveTextContent('Your title: Runbook')

    // The three designed choices are all offered.
    expect(screen.getByRole('button', { name: 'Keep editing' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Overwrite anyway' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy my text and cancel' })).toBeInTheDocument()
  })

  it('"Keep editing" dismisses the dialog and stays in the editor', async () => {
    renderEditPage(mutationError({ kind: 'StaleRevision', actualRevisionNumber: 7, latestContent: 'theirs\n' }))

    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Keep editing' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument()
  })

  it('"Copy my text and cancel" copies the draft to the clipboard, then leaves their revision standing', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(window.navigator, 'clipboard', { value: { writeText }, configurable: true })
    try {
      renderEditPage(mutationError({ kind: 'StaleRevision', actualRevisionNumber: 7, latestContent: 'theirs\n' }))

      fireEvent.click(await screen.findByRole('button', { name: 'Save' }))
      fireEvent.click(await screen.findByRole('button', { name: 'Copy my text and cancel' }))

      expect(await screen.findByText('view route')).toBeInTheDocument()
      expect(writeText).toHaveBeenCalledWith(expect.stringContaining('Hello world.'))
    } finally {
      Reflect.deleteProperty(window.navigator, 'clipboard')
    }
  })

  it('a refused clipboard write never navigates — the draft must not be silently destroyed', async () => {
    const writeText = vi.fn().mockRejectedValue(new Error('denied'))
    Object.defineProperty(window.navigator, 'clipboard', { value: { writeText }, configurable: true })
    try {
      renderEditPage(mutationError({ kind: 'StaleRevision', actualRevisionNumber: 7, latestContent: 'theirs\n' }))

      fireEvent.click(await screen.findByRole('button', { name: 'Save' }))
      fireEvent.click(await screen.findByRole('button', { name: 'Copy my text and cancel' }))

      expect(await screen.findByText(/Couldn't copy your draft/)).toBeInTheDocument()
      expect(screen.queryByText('view route')).toBeNull()
    } finally {
      Reflect.deleteProperty(window.navigator, 'clipboard')
    }
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

describe('PageEditPage permission gating', () => {
  it('refuses to mount an editor when the server says canEdit is false — no Save to be refused later', async () => {
    renderEditPage(null, { canEdit: false })

    expect(await screen.findByText(/don't have permission to edit/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Back to page' })).toBeInTheDocument()
  })
})
