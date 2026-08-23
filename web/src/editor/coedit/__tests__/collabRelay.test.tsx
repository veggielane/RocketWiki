import { afterEach, describe, expect, it, vi } from 'vitest'
import * as Y from 'yjs'
import { Editor } from '@tiptap/core'
import Collaboration from '@tiptap/extension-collaboration'
import { CollaborationCaret } from '@tiptap/extension-collaboration-caret'
import { editorExtensions } from '../../extensions'
import { jsonToMarkdown } from '../../markdown/toMarkdown'
import { renderCaret } from '../caretRender'
import { SignalRYjsProvider } from '../SignalRYjsProvider'
import type { CoEditTransport, EditSessionJoin, ReseedReason } from '../../../realtime/types'

/**
 * Editor-level integration: two REAL TipTap editors, two REAL providers,
 * REAL yjs docs — only the transport is a scripted in-memory relay that
 * mimics the hub's contract (NotificationsHub.EditSessions.cs: first
 * joiner of an empty-log session seeds; later joiners replay the log;
 * pushes append to the log and fan out to the other members). Typing in
 * editor A must appear in editor B through nothing but relayed bytes.
 */

const PAGE_ID = 'page-1'

/** The hub's session state: an update log plus connected members. */
class RelayHub {
  log: Uint8Array[] = []
  members = new Set<RelayTransport>()
  seederAssigned = false
}

class RelayTransport implements CoEditTransport {
  private readonly hub: RelayHub
  private updateHandlers = new Set<(pageId: string, update: Uint8Array) => void>()
  private awarenessHandlers = new Set<(pageId: string, update: Uint8Array) => void>()

  constructor(hub: RelayHub) {
    this.hub = hub
  }

  async joinEditSession(_pageId: string): Promise<EditSessionJoin | null> {
    this.hub.members.add(this)
    const isSeeder = this.hub.log.length === 0 && !this.hub.seederAssigned
    if (isSeeder) this.hub.seederAssigned = true
    return { role: isSeeder ? 'seeder' : 'joiner', baseRevisionNumber: 1, updateLog: [...this.hub.log] }
  }

  async leaveEditSession(_pageId: string): Promise<void> {
    this.hub.members.delete(this)
  }

  async pushUpdate(pageId: string, update: Uint8Array): Promise<void> {
    // Append before relay, like the hub (idempotent for a racing joiner).
    this.hub.log.push(update)
    for (const member of this.hub.members) {
      if (member === this) continue
      for (const handler of member.updateHandlers) handler(pageId, update)
    }
  }

  pushAwareness(pageId: string, update: Uint8Array): void {
    for (const member of this.hub.members) {
      if (member === this) continue
      for (const handler of member.awarenessHandlers) handler(pageId, update)
    }
  }

  async reseedEditSession(_pageId: string, fullState: Uint8Array): Promise<void> {
    this.hub.log = [fullState]
  }

  onUpdateReceived(handler: (pageId: string, update: Uint8Array) => void): () => void {
    this.updateHandlers.add(handler)
    return () => this.updateHandlers.delete(handler)
  }

  onAwarenessReceived(handler: (pageId: string, update: Uint8Array) => void): () => void {
    this.awarenessHandlers.add(handler)
    return () => this.awarenessHandlers.delete(handler)
  }

  onReseedRequired(_handler: (pageId: string, base: number, reason: ReseedReason) => void): () => void {
    return () => {}
  }

  onEvictedFromEditSession(_handler: (pageId: string) => void): () => void {
    return () => {}
  }

  onReconnected(_handler: () => void): () => void {
    return () => {}
  }
}

interface Member {
  provider: SignalRYjsProvider
  editor: Editor
  element: HTMLElement
}

async function joinAsEditor(hub: RelayHub, name: string, seedMarkdownDoc?: Y.Doc): Promise<Member> {
  const transport = new RelayTransport(hub)
  const provider = new SignalRYjsProvider({
    pageId: PAGE_ID,
    transport,
    seed: (doc, origin) => {
      // Editor-level seed: import a pre-built fragment (built by the test
      // via seedDoc.ts semantics) — or an empty doc for a blank page.
      if (seedMarkdownDoc) Y.applyUpdate(doc, Y.encodeStateAsUpdate(seedMarkdownDoc), origin)
    },
    updateFlushMs: 20,
    awarenessFlushMs: 20,
  })
  await provider.connect()
  expect(provider.status).toBe('collaborating')

  const element = document.createElement('div')
  document.body.appendChild(element)
  const editor = new Editor({
    element,
    extensions: [
      ...editorExtensions.map((ext) => (ext.name === 'starterKit' ? ext.configure({ undoRedo: false }) : ext)),
      Collaboration.configure({ document: provider.doc }),
      CollaborationCaret.configure({
        provider,
        user: { name, color: 'hsl(120, 70%, 45%)' },
        render: renderCaret,
      }),
    ],
  })
  return { provider, editor, element }
}

function destroy(member: Member) {
  member.editor.destroy()
  member.provider.dispose()
  member.element.remove()
}

let members: Member[] = []

afterEach(async () => {
  // Flush anything y-tiptap batched onto a zero-timeout BEFORE dropping the
  // fake clock: its setMeta batcher parks pending work in a module-global
  // map that only a fired timeout resets — discarding the pending timer
  // (what useRealTimers does) would wedge that global for every later test
  // in this file and silently kill caret rendering. Real-timer production
  // code can't hit this; it is purely a fake-timer teardown hazard.
  if (vi.isFakeTimers()) await vi.runOnlyPendingTimersAsync()
  for (const m of members) destroy(m)
  members = []
  if (vi.isFakeTimers()) {
    await vi.runOnlyPendingTimersAsync()
    vi.useRealTimers()
  }
})

describe('two live editors over the scripted relay (real yjs, real TipTap, fake transport only)', () => {
  it('typing in A appears in B — and both serialize to the same Markdown', async () => {
    vi.useFakeTimers()
    const hub = new RelayHub()
    const a = await joinAsEditor(hub, 'Ada')
    const b = await joinAsEditor(hub, 'Grace')
    members.push(a, b)

    a.editor.commands.setContent({
      type: 'doc',
      content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Hello from A' }] }],
    })
    await vi.advanceTimersByTimeAsync(50) // let the batched push flush + relay

    expect(b.editor.getText()).toContain('Hello from A')

    b.editor.commands.insertContentAt(b.editor.state.doc.content.size, {
      type: 'paragraph',
      content: [{ type: 'text', text: 'And hello back from B' }],
    })
    await vi.advanceTimersByTimeAsync(50)

    expect(a.editor.getText()).toContain('And hello back from B')
    // Convergence at the serialization level too — the round-trip rule's
    // collaborative corollary: both members would save identical Markdown.
    expect(jsonToMarkdown(a.editor.getJSON())).toBe(jsonToMarkdown(b.editor.getJSON()))
  })

  it('a late joiner replays the log and lands on the same document', async () => {
    vi.useFakeTimers()
    const hub = new RelayHub()
    const a = await joinAsEditor(hub, 'Ada')
    members.push(a)
    a.editor.commands.setContent({
      type: 'doc',
      content: [{ type: 'paragraph', content: [{ type: 'text', text: 'written before C arrived' }] }],
    })
    await vi.advanceTimersByTimeAsync(50)

    const c = await joinAsEditor(hub, 'Clara')
    members.push(c)

    expect(c.editor.getText()).toContain('written before C arrived')
    expect(jsonToMarkdown(c.editor.getJSON())).toBe(jsonToMarkdown(a.editor.getJSON()))
  })

  it("caret smoke: A's selection travels the awareness channel and renders as a labelled caret in B's editor", async () => {
    vi.useFakeTimers()
    const hub = new RelayHub()
    const a = await joinAsEditor(hub, 'Ada')
    const b = await joinAsEditor(hub, 'Grace')
    members.push(a, b)

    a.editor.commands.setContent({
      type: 'doc',
      content: [{ type: 'paragraph', content: [{ type: 'text', text: 'caret target text' }] }],
    })
    await vi.advanceTimersByTimeAsync(50)

    // jsdom cannot genuinely focus contentEditable, and the caret plugin
    // both publishes AND clears the local cursor based on `hasFocus()` —
    // an unfocused A would immediately null its own cursor field. Stubbing
    // focus is the only fake here; everything else is the real pipeline:
    // A's selection → relative-position cursor on awareness → y-protocols
    // encoding → relay → B decodes → caret widget decoration in B's DOM.
    vi.spyOn(a.editor.view, 'hasFocus').mockReturnValue(true)
    a.editor.commands.setTextSelection(6)
    await vi.advanceTimersByTimeAsync(50)

    const aState = b.provider.awareness.getStates().get(a.provider.doc.clientID)
    expect(aState).toMatchObject({ user: { name: 'Ada' } })
    expect(aState?.cursor).not.toBeNull()
    const caretLabel = b.element.querySelector('.collaboration-carets__label')
    expect(caretLabel).not.toBeNull()
    expect(caretLabel!.textContent).toBe('Ada')
    expect(b.element.querySelector('.collaboration-carets__caret')).not.toBeNull()
  })
})
