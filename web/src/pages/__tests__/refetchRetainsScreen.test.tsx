import { describe, expect, it, vi } from 'vitest'
import { createRef } from 'react'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Client, Provider as UrqlProvider, type Exchange, type Operation } from 'urql'
import { Kind, type OperationDefinitionNode } from 'graphql'
import { filter, fromPromise, fromValue, mergeMap, pipe } from 'wonka'
import { PageViewPage } from '../PageViewPage'
import { RichTextEditor, type RichTextEditorHandle } from '../../editor/RichTextEditor'

vi.mock('../../realtime/transports', async () => {
  const { FakePresenceTransport } = await import('../../realtime/FakePresenceTransport')
  const transport = new FakePresenceTransport()
  return { getDefaultPresenceTransport: () => transport }
})

/**
 * A write refetches (`requestPolicy: 'network-only'`), and urql keeps the
 * previous `data` while `fetching` is true (urql.js `computeNextState`). A bare
 * `if (fetching)` early return therefore threw the entire screen away on every
 * mutation: title, marking, content, attachments and the whole comment thread
 * became two grey rectangles, everything remounted, scroll position was lost,
 * and — the part no axe rule can see — keyboard focus was dropped to `<body>`,
 * so the next Tab restarted from the top of the document.
 *
 * The guard is `fetching && !data`: skeleton when there is nothing to show,
 * never on a re-read.
 *
 * Driven through the Watch toggle rather than the comment composer: posting a
 * comment needs text in a ProseMirror document, which cannot be entered through
 * `fireEvent` in jsdom (the view reads DOM mutations asynchronously). Watch is
 * the same shape — mutation, then `refetch({ requestPolicy: 'network-only' })`
 * — and needs no typing. The composer's own half of this is covered below.
 */

const basePage = {
  id: 'page-1',
  spaceId: 'space-1',
  spaceKey: 'ENG',
  title: 'Runbook',
  slug: 'runbook',
  icon: null as string | null,
  content: 'Hello world.\n',
  currentRevisionNumber: 3,
  canEdit: false,
  canComment: true,
  canManageAccess: false,
  viewerIsWatching: false,
  labels: [],
  labelDetails: [],
  marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [] as string[], prefix: 'UK', label: 'UK OFFICIAL' },
  properties: [],
  parent: null,
  children: [],
  comments: [],
  attachments: [],
}

function operationName(op: Operation): string {
  const def = op.query.definitions.find((d): d is OperationDefinitionNode => d.kind === Kind.OPERATION_DEFINITION)
  return def?.name?.value ?? '(anonymous)'
}

/**
 * `createMockUrqlClient` answers every operation SYNCHRONOUSLY, so React batches
 * the mutation result and the refetch response into one commit and the
 * intermediate `fetching` render never reaches the DOM — the teardown bug is
 * invisible to it. (Verified: these tests passed against the old `if (fetching)`
 * until this client replaced it.)
 *
 * This one holds the refetch open until the test releases it, which is what a
 * real network does and the only way to observe the state the bug lived in.
 */
function createDeferredClient(respond: (name: string) => Record<string, unknown> | undefined) {
  const names: string[] = []
  let pageReads = 0
  let release!: () => void
  const held = new Promise<void>((resolve) => {
    release = resolve
  })

  const exchange: Exchange = () => (ops$) =>
    pipe(
      ops$,
      filter((op: Operation) => op.kind !== 'teardown'),
      mergeMap((op: Operation) => {
        const name = operationName(op)
        names.push(name)
        const result = { operation: op, data: respond(name), stale: false, hasNext: false }
        // The FIRST page read resolves at once so the screen can mount; the
        // refetch that a write triggers is what gets held.
        if (name === 'PageById' && ++pageReads > 1) {
          return fromPromise(held.then(() => result))
        }
        return fromValue(result)
      }),
    )

  return {
    client: new Client({ url: '/graphql', exchanges: [exchange] }),
    names,
    release,
  }
}

const RESPONSES: Record<string, Record<string, unknown> | undefined> = {
  PageById: { page: basePage },
  CurrentUser: {
    me: {
      id: 'sub-1',
      email: null,
      name: 'Viewer',
      groups: [],
      isAuthenticated: true,
      isInstanceAdmin: false,
      localUserId: 'user-1',
    },
  },
  SpaceReplicaBanner: { space: { id: 'space-1', key: 'ENG', isReplica: false, originInstanceId: 'HIGH' } },
  WatchPage: { watchPage: { watch: { pageId: 'page-1' }, error: null } },
  SpaceTreeForMove: { pageTree: [] },
  SpaceLabelDetails: { labelDetails: [] },
}

function renderPage() {
  const deferred = createDeferredClient((name) => RESPONSES[name])
  render(
    <MemoryRouter initialEntries={['/pages/page-1']}>
      <UrqlProvider value={deferred.client}>
        <Routes>
          <Route path="/pages/:pageId" element={<PageViewPage />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return deferred
}

/** Presses Watch and waits until the refetch it triggers is in flight (and held). */
async function writeAndHoldRefetch(deferred: ReturnType<typeof createDeferredClient>) {
  fireEvent.click(screen.getByRole('button', { name: 'Watch' }))
  await waitFor(() => expect(deferred.names).toContain('WatchPage'))
  await waitFor(() => expect(deferred.names.filter((n) => n === 'PageById').length).toBeGreaterThan(1))
}

describe('a write does not tear the screen down', () => {
  it('keeps the very same heading node while a post-write refetch is in flight', async () => {
    // Identity, not presence: a teardown produces an equal-LOOKING heading once
    // the refetch lands, which `toBeInTheDocument()` would happily pass.
    const deferred = renderPage()
    const heading = await screen.findByRole('heading', { name: 'Runbook' })

    await writeAndHoldRefetch(deferred)

    // Asserted while the read is still open — the exact window the bug lived in.
    expect(screen.getByRole('heading', { name: 'Runbook' })).toBe(heading)
    expect(screen.getByText('Hello world.')).toBeInTheDocument()

    await act(async () => {
      deferred.release()
    })
    expect(screen.getByRole('heading', { name: 'Runbook' })).toBe(heading)
  })

  it('shows no skeleton while a post-write refetch is in flight', async () => {
    const deferred = renderPage()
    await screen.findByRole('heading', { name: 'Runbook' })

    await writeAndHoldRefetch(deferred)

    // MUI Skeleton renders a bare styled <span> with no role or name — which is
    // also why every loading state in the app is silent to a screen reader — so
    // it is queried by class, and its absence is the assertion.
    expect(document.querySelector('.MuiSkeleton-root')).toBeNull()

    await act(async () => {
      deferred.release()
    })
  })

  it('does not drop keyboard focus while a post-write refetch is in flight', async () => {
    // The keyboard consequence of the teardown, and the one a screen-reader user
    // feels hardest: focus fell to <body>, so the next Tab restarted from the
    // top of the document. Nothing in the axe suite can see this.
    const deferred = renderPage()
    await screen.findByRole('heading', { name: 'Runbook' })

    const watch = screen.getByRole('button', { name: 'Watch' })
    watch.focus()
    expect(document.activeElement).toBe(watch)

    await writeAndHoldRefetch(deferred)

    expect(document.activeElement).not.toBe(document.body)
    expect(document.activeElement).toBe(watch)

    await act(async () => {
      deferred.release()
    })
  })

  // No first-load-skeleton case: the first read resolves synchronously here, so
  // `fetching && !data` is never true for a render this harness can observe.
  // That direction of the guard is unchanged in meaning — `!data` is literally
  // "nothing to show yet".
})

/**
 * The composer's half. It appeared to clear itself after posting, but only
 * because the refetch above remounted the whole subtree underneath it — so
 * fixing the teardown without this would have left the text sitting in the box
 * after every successful post.
 */
describe('RichTextEditor.clear', () => {
  it('empties the document', () => {
    const handle = createRef<RichTextEditorHandle>()
    render(<RichTextEditor ref={handle} initialMarkdown="Draft text." showToolbar={false} ariaLabel="New comment" />)
    expect(handle.current!.getMarkdown()).toBe('Draft text.\n')

    handle.current!.clear()
    expect(handle.current!.getMarkdown().trim()).toBe('')
  })

  it('does not report the clear as an edit', () => {
    // A composer that marked itself dirty when it emptied would arm the
    // unsaved-changes guard on a post that had just succeeded.
    const onDocChanged = vi.fn()
    const handle = createRef<RichTextEditorHandle>()
    render(
      <RichTextEditor
        ref={handle}
        initialMarkdown="Draft text."
        showToolbar={false}
        ariaLabel="New comment"
        onDocChanged={onDocChanged}
      />,
    )
    handle.current!.clear()
    expect(onDocChanged).not.toHaveBeenCalled()
  })
})
