import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { createRef } from 'react'
import * as Y from 'yjs'
import { Awareness } from 'y-protocols/awareness'
import { RichTextEditor, type RichTextEditorHandle } from '../../RichTextEditor'
import { renderMermaid } from '../../diagrams/mermaidRenderer'
import { seedDocFromMarkdown } from '../seedDoc'

/**
 * The recently-merged render features must keep working INSIDE the
 * collaborative editor: mermaid NodeViews and the emoji machinery are
 * schema-neutral (rendering-only — extensions.ts documents the swap rule),
 * so binding the document to a Y.Doc must change nothing about them. This
 * renders the REAL RichTextEditor in collab mode over a seeded Y.Doc —
 * exactly what a joiner sees — and asserts the NodeView renders, the
 * literal text survives, and serialization stays byte-identical.
 *
 * Mermaid is mocked at the renderer seam like mermaidCodeBlock.test.tsx
 * (jsdom lacks the SVG APIs the real renderer needs).
 */
vi.mock('../../diagrams/mermaidRenderer', () => ({
  renderMermaid: vi.fn(),
}))

const renderMermaidMock = vi.mocked(renderMermaid)

beforeEach(() => {
  renderMermaidMock.mockReset()
})

const MARKDOWN = 'Ship it :rocket: today.\n\n```mermaid\ngraph TD\n  A --> B\n```\n'

function collabEditor(markdown: string) {
  const doc = new Y.Doc()
  seedDocFromMarkdown(doc, markdown, 'test')
  const awareness = new Awareness(doc)
  const handle = createRef<RichTextEditorHandle>()
  const view = render(
    <RichTextEditor
      ref={handle}
      initialMarkdown="THIS MUST NOT RENDER" // collab mode ignores it — the Y.Doc is the document
      editable
      showToolbar={false}
      collab={{ doc, provider: { awareness }, user: { name: 'Ada', color: 'hsl(1, 70%, 45%)' } }}
    />,
  )
  return { doc, awareness, handle, view }
}

describe('schema-neutral render features inside the collaborative editor', () => {
  it('renders the mermaid NodeView (live preview) from the seeded Y.Doc, not from initialMarkdown', async () => {
    renderMermaidMock.mockResolvedValue({ status: 'ok', svg: '<svg data-marker="mermaid-out"></svg>' })
    const { view } = collabEditor(MARKDOWN)

    await screen.findByRole('img', { name: 'Mermaid diagram' })
    expect(renderMermaidMock).toHaveBeenCalledExactlyOnceWith('graph TD\n  A --> B')
    expect(view.container.querySelector('.rw-mermaid-block')).toHaveClass('rw-mermaid-block-editing')
    expect(view.container.textContent).not.toContain('THIS MUST NOT RENDER')
  })

  it('emoji stays literal text while editing (the decoration is read-mode-only by design) and the doc round-trips byte-identical', async () => {
    renderMermaidMock.mockResolvedValue({ status: 'ok', svg: '<svg></svg>' })
    const { handle, view } = collabEditor(MARKDOWN)
    await screen.findByRole('img', { name: 'Mermaid diagram' })

    // The literal `:rocket:` is the source of truth the author manipulates
    // in edit mode (EmojiDecorations.ts) — collab must not change that.
    expect(view.container.textContent).toContain(':rocket:')
    // And the whole document — emoji text, mermaid fence — serializes back
    // byte-identical through the Y.Doc binding: the round-trip rule holds
    // in collaborative mode.
    expect(handle.current!.getMarkdown()).toBe(MARKDOWN)
  })

  it('a remote edit arriving through the Y.Doc updates what the local editor serializes', async () => {
    renderMermaidMock.mockResolvedValue({ status: 'ok', svg: '<svg></svg>' })
    const { doc, handle } = collabEditor(MARKDOWN)
    await screen.findByRole('img', { name: 'Mermaid diagram' })

    // A peer doc (same lineage) appends a paragraph; its update lands in
    // our Y.Doc exactly as UpdateReceived would deliver it.
    const peer = new Y.Doc()
    Y.applyUpdate(peer, Y.encodeStateAsUpdate(doc))
    const fragment = peer.getXmlFragment('default')
    const paragraph = new Y.XmlElement('paragraph')
    paragraph.insert(0, [new Y.XmlText('Added by a peer.')])
    fragment.push([paragraph])
    Y.applyUpdate(doc, Y.encodeStateAsUpdate(peer, Y.encodeStateVector(doc)))

    expect(handle.current!.getMarkdown()).toBe(`${MARKDOWN}\nAdded by a peer.\n`)
  })
})
