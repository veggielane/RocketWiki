import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as Y from 'yjs'
import { Awareness, applyAwarenessUpdate } from 'y-protocols/awareness'
import { FakePresenceTransport } from '../../../realtime/FakePresenceTransport'
import { SignalRYjsProvider, type CoEditStatus } from '../SignalRYjsProvider'

/**
 * Provider unit tests against the scripted fake hub transport
 * (FakePresenceTransport doubles as CoEditTransport, exactly like the real
 * class). ONLY the transport is fake: yjs and y-protocols run for real, so
 * what's asserted is genuine CRDT behavior — replay order, idempotent
 * merges, real awareness protocol bytes — not a mock of it.
 */

const PAGE_ID = 'page-1'

/** Seeds the doc's shared text with the given string — a schema-free stand-in for the Markdown seeding the editor layer does. */
function textSeed(content: string) {
  return (doc: Y.Doc, origin: unknown) => {
    Y.transact(doc, () => doc.getText('t').insert(0, content), origin)
  }
}

function createProvider(
  transport: FakePresenceTransport,
  overrides: Partial<ConstructorParameters<typeof SignalRYjsProvider>[0]> = {},
) {
  const statuses: CoEditStatus[] = []
  const baseRevisions: number[] = []
  const reseedDemands: number[] = []
  const provider = new SignalRYjsProvider({
    pageId: PAGE_ID,
    transport,
    seed: textSeed('seeded content'),
    onStatusChange: (s) => statuses.push(s),
    onBaseRevisionChange: (n) => baseRevisions.push(n),
    onSaveAndReseedRequired: (n) => reseedDemands.push(n),
    updateFlushMs: 50,
    awarenessFlushMs: 50,
    ...overrides,
  })
  return { provider, statuses, baseRevisions, reseedDemands }
}

/** Decodes what a peer would hold after applying every pushed update in order. */
function docFromPushes(transport: FakePresenceTransport): Y.Doc {
  const doc = new Y.Doc()
  for (const { update } of transport.pushedUpdates) {
    Y.applyUpdate(doc, update)
  }
  return doc
}

let disposables: SignalRYjsProvider[] = []

beforeEach(() => {
  vi.useFakeTimers()
  disposables = []
})

afterEach(() => {
  for (const p of disposables) p.dispose()
  vi.useRealTimers()
})

function track(provider: SignalRYjsProvider): SignalRYjsProvider {
  disposables.push(provider)
  return provider
}

describe('join roles', () => {
  it('seeder: builds the doc from saved content and pushes the encoded full state as its first update', async () => {
    const transport = new FakePresenceTransport()
    transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 7, updateLog: [] }
    const { provider, statuses, baseRevisions } = createProvider(transport)
    track(provider)

    await provider.connect()

    expect(provider.status).toBe('collaborating')
    expect(statuses).toEqual(['collaborating'])
    expect(provider.baseRevisionNumber).toBe(7)
    expect(baseRevisions).toEqual([7])
    expect(provider.doc.getText('t').toString()).toBe('seeded content')
    // The seed rides exactly one explicit full-state push — never a second
    // copy through the ordinary update queue.
    expect(transport.pushedUpdates).toHaveLength(1)
    await vi.advanceTimersByTimeAsync(200)
    expect(transport.pushedUpdates).toHaveLength(1)
    expect(docFromPushes(transport).getText('t').toString()).toBe('seeded content')
  })

  it('joiner: replays the update log in order into a fresh doc', async () => {
    // Build a genuine incremental log: two consecutive edits from a peer.
    const source = new Y.Doc()
    const log: Uint8Array[] = []
    source.on('update', (u: Uint8Array) => log.push(u))
    source.getText('t').insert(0, 'hello')
    source.getText('t').insert(5, ' world')
    expect(log).toHaveLength(2)

    const transport = new FakePresenceTransport()
    transport.editSessionJoinResult = { role: 'joiner', baseRevisionNumber: 3, updateLog: log }
    const { provider } = createProvider(transport)
    track(provider)

    await provider.connect()

    expect(provider.status).toBe('collaborating')
    expect(provider.baseRevisionNumber).toBe(3)
    expect(provider.doc.getText('t').toString()).toBe('hello world')
    // Replayed content is not echoed back to the hub.
    expect(transport.pushedUpdates).toHaveLength(0)
  })

  it('a refused join (null — indistinguishable from nonexistent by design) lands in solo, silently', async () => {
    const transport = new FakePresenceTransport()
    const { provider, statuses } = createProvider(transport)
    track(provider)

    await provider.connect()

    expect(provider.status).toBe('solo')
    expect(statuses).toEqual(['solo'])
    // Solo means the doc is unwired: local edits push nothing.
    provider.doc.getText('t').insert(0, 'local only')
    await vi.advanceTimersByTimeAsync(200)
    expect(transport.pushedUpdates).toHaveLength(0)
  })

  it('a join that outlives the timeout falls back to solo — and withdraws if the join lands late', async () => {
    const transport = new FakePresenceTransport()
    let resolveJoin: (v: { role: 'joiner'; baseRevisionNumber: number; updateLog: Uint8Array[] }) => void = () => {}
    const pending = new Promise<{ role: 'joiner'; baseRevisionNumber: number; updateLog: Uint8Array[] }>((r) => {
      resolveJoin = r
    })
    transport.joinEditSession = () => pending
    const { provider } = createProvider(transport, { joinTimeoutMs: 1000 })
    track(provider)

    const connecting = provider.connect()
    await vi.advanceTimersByTimeAsync(1001)
    await connecting
    expect(provider.status).toBe('solo')

    // The hub eventually answered yes — but we already run solo, so ghost
    // membership must be withdrawn (it would hold the session open and
    // mis-credit contributors).
    resolveJoin({ role: 'joiner', baseRevisionNumber: 1, updateLog: [] })
    await vi.advanceTimersByTimeAsync(1)
    expect(transport.leftEditSessions).toEqual([PAGE_ID])
  })
})

describe('update relay', () => {
  async function collaboratingProvider(transport: FakePresenceTransport) {
    transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 1, updateLog: [] }
    const created = createProvider(transport)
    track(created.provider)
    await created.provider.connect()
    transport.pushedUpdates.length = 0 // drop the seed push; these tests assert about later traffic
    return created
  }

  it('batches keystroke-sized local edits into one merged push per flush window', async () => {
    const transport = new FakePresenceTransport()
    const { provider } = await collaboratingProvider(transport)
    // A peer holding the session state so far (same CRDT lineage — Yjs
    // updates are relative to shared history, so an unrelated doc could
    // not integrate the frame).
    const peer = new Y.Doc()
    Y.applyUpdate(peer, Y.encodeStateAsUpdate(provider.doc))

    provider.doc.getText('t').insert(14, '!')
    provider.doc.getText('t').insert(15, '?')
    provider.doc.getText('t').insert(16, '.')
    expect(transport.pushedUpdates).toHaveLength(0) // nothing sent per keystroke

    await vi.advanceTimersByTimeAsync(60)
    expect(transport.pushedUpdates).toHaveLength(1) // one merged frame

    // The single frame carries all three edits.
    Y.applyUpdate(peer, transport.pushedUpdates[0].update)
    expect(peer.getText('t').toString()).toBe('seeded content!?.')
  })

  it('applies incoming updates and never echoes them back', async () => {
    const transport = new FakePresenceTransport()
    const { provider } = await collaboratingProvider(transport)

    const peer = new Y.Doc()
    Y.applyUpdate(peer, Y.encodeStateAsUpdate(provider.doc))
    peer.getText('t').insert(0, 'peer: ')
    transport.emitUpdate(PAGE_ID, Y.encodeStateAsUpdate(peer, Y.encodeStateVector(provider.doc)))

    expect(provider.doc.getText('t').toString()).toBe('peer: seeded content')
    await vi.advanceTimersByTimeAsync(200)
    expect(transport.pushedUpdates).toHaveLength(0)
  })

  it("ignores updates for other pages — a relayed frame only ever lands in its own page's doc", async () => {
    const transport = new FakePresenceTransport()
    const { provider } = await collaboratingProvider(transport)

    const other = new Y.Doc()
    other.getText('t').insert(0, 'other page content')
    transport.emitUpdate('some-other-page', Y.encodeStateAsUpdate(other))

    expect(provider.doc.getText('t').toString()).toBe('seeded content')
  })

  it('an oversized single update (> 512 KiB) is not sent — the honest local signal fires instead', async () => {
    const transport = new FakePresenceTransport()
    let oversized = 0
    transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 1, updateLog: [] }
    const { provider } = createProvider(transport, { onOversizedUpdate: () => oversized++ })
    track(provider)
    await provider.connect()
    transport.pushedUpdates.length = 0

    provider.doc.getText('t').insert(0, 'x'.repeat(600 * 1024)) // one colossal paste, one transaction
    await vi.advanceTimersByTimeAsync(60)

    expect(transport.pushedUpdates).toHaveLength(0)
    expect(oversized).toBe(1)
  })
})

describe('awareness (carets/selections — real y-protocols encoding both ways)', () => {
  it('throttles local awareness changes into one encoded frame that a real peer Awareness decodes', async () => {
    const transport = new FakePresenceTransport()
    transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 1, updateLog: [] }
    const { provider } = createProvider(transport)
    track(provider)
    await provider.connect()

    provider.awareness.setLocalStateField('user', { name: 'Ada', color: 'hsl(1, 70%, 45%)' })
    provider.awareness.setLocalStateField('cursor', { anchor: 1, head: 1 })
    provider.awareness.setLocalStateField('cursor', { anchor: 2, head: 2 })
    expect(transport.pushedAwareness).toHaveLength(0) // throttled, not per-change

    await vi.advanceTimersByTimeAsync(60)
    expect(transport.pushedAwareness).toHaveLength(1)

    // Round trip through the REAL protocol: a peer's Awareness instance
    // decodes the frame to Ada's latest state.
    const peerDoc = new Y.Doc()
    const peerAwareness = new Awareness(peerDoc)
    applyAwarenessUpdate(peerAwareness, transport.pushedAwareness[0].update, 'remote')
    const state = peerAwareness.getStates().get(provider.doc.clientID)
    expect(state).toMatchObject({ user: { name: 'Ada' }, cursor: { anchor: 2, head: 2 } })
    peerAwareness.destroy()
    peerDoc.destroy()
  })

  it('applies incoming awareness frames to its own Awareness without echoing them back', async () => {
    const transport = new FakePresenceTransport()
    transport.editSessionJoinResult = { role: 'joiner', baseRevisionNumber: 1, updateLog: [] }
    const { provider } = createProvider(transport)
    track(provider)
    await provider.connect()

    const peerDoc = new Y.Doc()
    const peerAwareness = new Awareness(peerDoc)
    peerAwareness.setLocalStateField('user', { name: 'Grace', color: 'hsl(2, 70%, 45%)' })
    const frame = (await import('y-protocols/awareness')).encodeAwarenessUpdate(peerAwareness, [peerDoc.clientID])
    transport.emitAwareness(PAGE_ID, frame)

    expect(provider.awareness.getStates().get(peerDoc.clientID)).toMatchObject({ user: { name: 'Grace' } })
    await vi.advanceTimersByTimeAsync(200)
    expect(transport.pushedAwareness).toHaveLength(0)
    peerAwareness.destroy()
    peerDoc.destroy()
  })
})

describe('reseed', () => {
  it('log_cap: surfaces the save demand; completeReseed hands back the full doc state and advances the base', async () => {
    const transport = new FakePresenceTransport()
    transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 5, updateLog: [] }
    const { provider, reseedDemands, baseRevisions } = createProvider(transport)
    track(provider)
    await provider.connect()

    transport.emitReseedRequired(PAGE_ID, 5, 'log_cap')
    expect(reseedDemands).toEqual([5])
    expect(provider.pendingReseed).toBe(5)

    await provider.completeReseed(6)
    expect(provider.pendingReseed).toBeNull()
    expect(provider.baseRevisionNumber).toBe(6)
    expect(baseRevisions).toEqual([5, 6])
    expect(transport.reseeds).toHaveLength(1)

    // The snapshot IS the doc: a fresh joiner applying only it holds everything.
    const joiner = new Y.Doc()
    Y.applyUpdate(joiner, transport.reseeds[0].fullState)
    expect(joiner.getText('t').toString()).toBe('seeded content')
  })

  it('seeder_lost (pre-seed only): the promoted joiner seeds and pushes the full state', async () => {
    const transport = new FakePresenceTransport()
    transport.editSessionJoinResult = { role: 'joiner', baseRevisionNumber: 2, updateLog: [] }
    const { provider, reseedDemands } = createProvider(transport)
    track(provider)
    await provider.connect()
    expect(provider.doc.getText('t').toString()).toBe('') // waiting for a seed that will never come

    transport.emitReseedRequired(PAGE_ID, 2, 'seeder_lost')

    expect(provider.doc.getText('t').toString()).toBe('seeded content')
    await vi.advanceTimersByTimeAsync(1)
    expect(transport.pushedUpdates).toHaveLength(1)
    expect(docFromPushes(transport).getText('t').toString()).toBe('seeded content')
    // seeder_lost is a promotion, not a save demand.
    expect(reseedDemands).toEqual([])
  })
})

describe('eviction and teardown', () => {
  it('eviction tears down: read-only status, no further pushes in, no application of incoming frames', async () => {
    const transport = new FakePresenceTransport()
    transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 1, updateLog: [] }
    const { provider, statuses } = createProvider(transport)
    track(provider)
    await provider.connect()
    transport.pushedUpdates.length = 0

    transport.emitEvicted(PAGE_ID)
    expect(provider.status).toBe('evicted')
    expect(statuses).toEqual(['collaborating', 'evicted'])

    // Nothing out…
    provider.doc.getText('t').insert(0, 'after eviction ')
    await vi.advanceTimersByTimeAsync(200)
    expect(transport.pushedUpdates).toHaveLength(0)
    // …nothing in…
    const peer = new Y.Doc()
    peer.getText('t').insert(0, 'peer content')
    transport.emitUpdate(PAGE_ID, Y.encodeStateAsUpdate(peer))
    expect(provider.doc.getText('t').toString()).toBe('after eviction seeded content')
    // …and no rejoin loop.
    transport.emitReconnected()
    await vi.advanceTimersByTimeAsync(10)
    expect(transport.joinedEditSessions).toEqual([PAGE_ID])
  })

  it('dispose flushes pending edits, withdraws awareness (peers see the caret go), and leaves the session', async () => {
    const transport = new FakePresenceTransport()
    transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 1, updateLog: [] }
    const { provider } = createProvider(transport)
    track(provider)
    await provider.connect()
    provider.awareness.setLocalStateField('user', { name: 'Ada', color: 'hsl(1, 70%, 45%)' })
    await vi.advanceTimersByTimeAsync(60)
    transport.pushedUpdates.length = 0
    const clientId = provider.doc.clientID

    provider.doc.getText('t').insert(0, 'last words ') // still unflushed…
    provider.dispose()

    // …but flushed by dispose, not dropped.
    expect(transport.pushedUpdates).toHaveLength(1)
    expect(transport.leftEditSessions).toEqual([PAGE_ID])

    // The final awareness frame removes this client on a real peer.
    const peerDoc = new Y.Doc()
    const peerAwareness = new Awareness(peerDoc)
    for (const { update } of transport.pushedAwareness) {
      applyAwarenessUpdate(peerAwareness, update, 'remote')
    }
    expect(peerAwareness.getStates().has(clientId)).toBe(false)
    peerAwareness.destroy()
    peerDoc.destroy()
  })

  it('dispose while the join is in flight withdraws a late-succeeding membership', async () => {
    const transport = new FakePresenceTransport()
    let resolveJoin: (v: { role: 'seeder'; baseRevisionNumber: number; updateLog: Uint8Array[] }) => void = () => {}
    transport.joinEditSession = () =>
      new Promise((r) => {
        resolveJoin = r
      })
    const { provider } = createProvider(transport)

    const connecting = provider.connect()
    provider.dispose()
    resolveJoin({ role: 'seeder', baseRevisionNumber: 1, updateLog: [] })
    await connecting

    expect(transport.leftEditSessions).toEqual([PAGE_ID])
  })
})

describe('reconnect (see the provider class doc for what this preserves, honestly)', () => {
  it('same session survived: replays missed peer updates and re-pushes its own full state', async () => {
    const transport = new FakePresenceTransport()
    transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 4, updateLog: [] }
    const { provider } = createProvider(transport)
    track(provider)
    await provider.connect()

    // While we were disconnected: a peer (sharing our lineage) edited.
    const peer = new Y.Doc()
    Y.applyUpdate(peer, Y.encodeStateAsUpdate(provider.doc))
    peer.getText('t').insert(0, 'peer-while-away: ')
    // And we edited locally, unsynced.
    provider.doc.getText('t').insert(provider.doc.getText('t').length, ' + my-offline-edit')
    transport.pushedUpdates.length = 0

    // The rejoin returns the session's full log (seed + the peer's edit).
    transport.editSessionJoinResult = {
      role: 'joiner',
      baseRevisionNumber: 4,
      updateLog: [Y.encodeStateAsUpdate(peer)],
    }
    transport.emitReconnected()
    await vi.advanceTimersByTimeAsync(10)

    // Merged, not duplicated (shared history — the normal case)…
    expect(provider.doc.getText('t').toString()).toBe('peer-while-away: seeded content + my-offline-edit')
    // …and the full-state push carries our offline edit back to the session.
    expect(transport.pushedUpdates).toHaveLength(1)
    const rejoined = new Y.Doc()
    Y.applyUpdate(rejoined, transport.pushedUpdates[0].update)
    expect(rejoined.getText('t').toString()).toBe('peer-while-away: seeded content + my-offline-edit')
  })

  it('session dropped, we are re-designated seeder: seeds from our OWN live doc, preserving unsaved edits', async () => {
    const transport = new FakePresenceTransport()
    transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 4, updateLog: [] }
    const { provider, baseRevisions } = createProvider(transport)
    track(provider)
    await provider.connect()
    provider.doc.getText('t').insert(0, 'unsaved-live-edit ')
    await vi.advanceTimersByTimeAsync(60)
    transport.pushedUpdates.length = 0

    transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 4, updateLog: [] }
    transport.emitReconnected()
    await vi.advanceTimersByTimeAsync(10)

    expect(transport.pushedUpdates).toHaveLength(1)
    const rejoined = new Y.Doc()
    Y.applyUpdate(rejoined, transport.pushedUpdates[0].update)
    // NOT re-seeded from saved content — the live doc superset is the seed.
    expect(rejoined.getText('t').toString()).toBe('unsaved-live-edit seeded content')
    expect(baseRevisions).toEqual([4, 4])
  })

  it('rejoin refused (canEdit revoked while offline): terminal read-only, no retry loop', async () => {
    const transport = new FakePresenceTransport()
    transport.editSessionJoinResult = { role: 'seeder', baseRevisionNumber: 4, updateLog: [] }
    const { provider } = createProvider(transport)
    track(provider)
    await provider.connect()

    transport.editSessionJoinResult = null
    transport.emitReconnected()
    await vi.advanceTimersByTimeAsync(10)

    expect(provider.status).toBe('evicted')
    transport.emitReconnected()
    await vi.advanceTimersByTimeAsync(10)
    expect(transport.joinedEditSessions).toEqual([PAGE_ID, PAGE_ID]) // initial + one rejoin, then never again
  })
})
