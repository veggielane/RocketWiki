import { beforeEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'

import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import * as Y from 'yjs'
import { yDocToProsemirrorJSON } from '@tiptap/y-tiptap'
import { PageEditPage } from '../PageEditPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { jsonToMarkdown } from '../../editor/markdown/toMarkdown'
import { expectNoAxeViolations } from '../../test/axe'
import { FakePresenceTransport } from '../../realtime/FakePresenceTransport'
import type { MutationErrorFragment } from '../../graphql/generated/graphql'

// The edit page joins presence AND (since co-editing) the page's edit
// session on mount; tests must never construct a real SignalR connection
// (design.md §8 — and jsdom has no hub to reach). One fake serves both
// interfaces — exactly like the real transport, which is one object on one
// connection. A fresh instance per test (assigned in beforeEach) keeps
// scripted join results and recorded traffic from leaking across tests.
const holder = vi.hoisted(() => ({ transport: undefined as unknown }))
vi.mock('../../realtime/transports', () => ({
  getDefaultPresenceTransport: () => holder.transport,
  getDefaultCoEditTransport: () => holder.transport,
}))

let transport: FakePresenceTransport

beforeEach(() => {
  transport = new FakePresenceTransport()
  holder.transport = transport
})

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

const me = {
  id: 'subject-1',
  email: 'ada@example.test',
  name: 'Ada Lovelace',
  groups: [],
  isAuthenticated: true,
  isInstanceAdmin: false,
  localUserId: 'user-1',
  hasAvatar: false,
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

interface RenderOptions {
  pageOverrides?: Partial<typeof page>
  /** Response page for UpdatePageContentInSession (collab saves). */
  sessionSavedPage?: Record<string, unknown> | null
  sessionSaveError?: MutationErrorFragment | null
}

function renderEditPage(updateError: MutationErrorFragment | null, options: RenderOptions = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'PageById') return { page: { ...page, ...options.pageOverrides } }
    if (name === 'CurrentUser') return { me }
    if (name === 'UpdatePageContent') return { updatePageContent: { page: updateError ? null : page, error: updateError } }
    if (name === 'UpdatePageContentInSession')
      return {
        updatePageContent: {
          page: options.sessionSaveError ? null : (options.sessionSavedPage ?? null),
          error: options.sessionSaveError ?? null,
        },
      }
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
 * reconciliation moved this page onto. The co-edit transport joins with
 * its default `null` (refused) here, so all of these exercise the SOLO
 * path — which must behave exactly as before co-editing existed.
 */
describe('PageEditPage accessibility', () => {
  it('has no axe violations with the editor and formatting toolbar mounted', async () => {
    renderEditPage(null)
    await screen.findByRole('toolbar', { name: 'Formatting' })
    await screen.findByRole('button', { name: 'Save' })
    await expectNoAxeViolations()
  })
})

describe('PageEditPage typed mutation errors', () => {
  it('ReadOnlyReplica opens the replica dialog with originInstanceId carried through — never a raw error', async () => {
    renderEditPage(mutationError({ kind: 'ReadOnlyReplica', spaceId: 'space-1', originInstanceId: 'LOW' }))

    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))

    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('read-only')
    // design.md §12: "Replica of LOW — read-only" needs the origin id.
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
    renderEditPage(null, { pageOverrides: { canEdit: false } })

    expect(await screen.findByText(/don't have permission to edit/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Back to page' })).toBeInTheDocument()
  })
})

describe('PageEditPage solo fallback (co-editing is a progressive enhancement, never a regression)', () => {
  it('a refused edit-session join renders the plain solo editor: no Live chip, saves ride the original mutation with the page-query revision', async () => {
    const mock = renderEditPage(null)

    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))
    expect(await screen.findByText('view route')).toBeInTheDocument()

    // The join was attempted (progressive enhancement) and refused —
    // silently, indistinguishable from nonexistence (design.md §6.7).
    expect(transport.joinedEditSessions).toEqual(['page-1'])
    expect(screen.queryByText('Live co-editing')).toBeNull()
    const updates = mock.operations.filter((op) => op.name === 'UpdatePageContent')
    expect(updates).toHaveLength(1)
    expect((updates[0].variables['input'] as { expectedRevisionNumber: number }).expectedRevisionNumber).toBe(3)
    expect(mock.operations.filter((op) => op.name === 'UpdatePageContentInSession')).toHaveLength(0)
  })

  it('joins page presence on mount (pointer overlay rides the same canView-gated JoinPage channel as the view page)', async () => {
    renderEditPage(null)
    await screen.findByRole('button', { name: 'Save' })

    expect(transport.currentlyJoinedPages).toEqual(['page-1'])
    act(() => {
      transport.emitPointer({ userId: 'user-9', displayName: 'Zoe', colour: 'hsl(9, 70%, 45%)', x: 0.5, y: 0.5 })
    })
    expect(await screen.findByText('Zoe')).toBeInTheDocument()
  })
})

const sessionRevisions = (revisionNumber: number, contributors: string[]) => [
  {
    id: `rev-${revisionNumber}`,
    revisionNumber,
    contributors: contributors.map((displayName, i) => ({ id: `c-${i}`, displayName, hasAvatar: false })),
  },
]

async function renderCollabEditPage(options: RenderOptions = {}) {
  transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 41, updateLog: [] }
  const mock = renderEditPage(null, {
    sessionSavedPage: {
      ...page,
      currentRevisionNumber: 42,
      revisions: sessionRevisions(42, ['Ada Lovelace', 'Grace Hopper']),
    },
    ...options,
  })
  expect(await screen.findByText('Live co-editing')).toBeInTheDocument()
  return mock
}

describe('PageEditPage collaborative mode', () => {
  it('a successful join mounts the live editor; the seeder pushes the page content as the session seed', async () => {
    await renderCollabEditPage()

    // The first push is the encoded full state of the seeded Y.Doc: decode
    // it like a joiner would and it must serialize to the saved Markdown.
    expect(transport.pushedUpdates.length).toBeGreaterThan(0)
    const joiner = new Y.Doc()
    Y.applyUpdate(joiner, transport.pushedUpdates[0].update)
    expect(jsonToMarkdown(yDocToProsemirrorJSON(joiner, 'default'))).toBe('Hello world.\n')
  })

  it('save uses the SESSION base revision (not the page query snapshot) and then goes to the page', async () => {
    const mock = await renderCollabEditPage()

    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      const saves = mock.operations.filter((op) => op.name === 'UpdatePageContentInSession')
      expect(saves).toHaveLength(1)
      // 41 from the join — page-1's query said 3, which would be stale.
      expect((saves[0].variables['input'] as { expectedRevisionNumber: number }).expectedRevisionNumber).toBe(41)
    })
    expect(mock.operations.filter((op) => op.name === 'UpdatePageContent')).toHaveLength(0)

    // Pressing Save leaves the editor even in a session. It used not to, on the
    // reasoning that a session save is a checkpoint rather than an exit — but
    // the session status turns 'collaborating' the moment the edit session is
    // joined, whether or not anyone else is in it, so the ordinary case of one
    // person editing alone never navigated and Save looked like it did nothing.
    expect(await screen.findByText('view route')).toBeInTheDocument()
  })

  it('log_cap ReseedRequired auto-saves with the demanded base, hands back the snapshot, and the NEXT save uses the advanced base', async () => {
    const mock = await renderCollabEditPage()

    act(() => {
      transport.emitReseedRequired('page-1', 41, 'log_cap')
    })

    // The auto-save (as this user — honest: the server credits saver +
    // contributors) and the snapshot handback, with only a toast shown.
    await waitFor(() => {
      const saves = mock.operations.filter((op) => op.name === 'UpdatePageContentInSession')
      expect(saves).toHaveLength(1)
      expect((saves[0].variables['input'] as { expectedRevisionNumber: number }).expectedRevisionNumber).toBe(41)
    })
    await waitFor(() => expect(transport.reseeds).toHaveLength(1))
    expect(await screen.findByText(/Autosaved revision 42/)).toBeInTheDocument()

    // An automatic save must NOT navigate, unlike pressing Save: the log-cap
    // reseed fires on the server's schedule, so moving the author here would
    // yank them out of the editor mid-sentence for background housekeeping.
    expect(screen.queryByText('view route')).toBeNull()
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument()

    // Base tracking after the reseed: the next manual save must submit
    // against 42, not 41 (the server advanced its copy the same way).
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => {
      const saves = mock.operations.filter((op) => op.name === 'UpdatePageContentInSession')
      expect(saves).toHaveLength(2)
      expect((saves[1].variables['input'] as { expectedRevisionNumber: number }).expectedRevisionNumber).toBe(42)
    })
  })

  it('eviction drops to read-only with clear copy — Save disabled, no rejoin, content still on screen to copy', async () => {
    await renderCollabEditPage()

    act(() => {
      transport.emitEvicted('page-1')
    })

    expect(await screen.findByText(/edit access to this page was revoked/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled()
    // No retry-join loop: still exactly the one original join.
    expect(transport.joinedEditSessions).toEqual(['page-1'])
    // The user's text is still there to copy.
    expect(screen.getByText('Hello world.')).toBeInTheDocument()
  })

  it('a StaleRevision on a session save still opens the existing merge flow (rare — another save path raced)', async () => {
    transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 41, updateLog: [] }
    renderEditPage(null, {
      sessionSaveError: mutationError({
        kind: 'StaleRevision',
        expectedRevisionNumber: 41,
        actualRevisionNumber: 44,
        latestContent: 'raced content\n',
      }),
    })
    expect(await screen.findByText('Live co-editing')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Someone else saved changes first')
    expect(dialog).toHaveTextContent('revision 44')
  })

  it('leaves the edit session on unmount — a leaked session membership is a live data leak, not just a memory leak', async () => {
    await renderCollabEditPage()

    fireEvent.click(screen.getByRole('button', { name: 'Close' }))

    expect(await screen.findByText('view route')).toBeInTheDocument()
    expect(transport.leftEditSessions).toEqual(['page-1'])
  })
})
