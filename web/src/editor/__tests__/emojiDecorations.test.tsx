import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import { RichTextEditor } from '../RichTextEditor'
import { setEmojiRegistry, resetEmojiRegistry } from '../../emoji/registry'
import { resetEmojiBlobCache } from '../../emoji/emojiBlobCache'

/**
 * Registry-gated `:name:` rendering in read mode (design.md §19), through
 * the real RichTextEditor: a registered name becomes an inline <img> via a
 * ProseMirror *decoration* (never a schema node — the round-trip corpus
 * carries the byte-identity proof), an unknown name stays literal text
 * with no error UI, and images arrive over the authenticated route as
 * blob: URLs.
 */

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(new Blob(['gif bytes']), { status: 200 })),
  )
  URL.createObjectURL = vi.fn(() => 'blob:emoji-url')
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  resetEmojiRegistry()
  resetEmojiBlobCache()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('emoji decorations in read mode', () => {
  it('renders a registered name as an inline image with alt/title `:name:`', async () => {
    setEmojiRegistry([{ name: 'rocket', etag: 'e1' }])
    render(<RichTextEditor initialMarkdown={'Launch :rocket: now.\n'} editable={false} showToolbar={false} />)

    const img = await screen.findByAltText(':rocket:')
    expect(img).toHaveAttribute('src', 'blob:emoji-url')
    expect(img).toHaveAttribute('title', ':rocket:')
    expect(img).toHaveClass('rw-emoji')
    expect(fetch).toHaveBeenCalledWith('/emojis/rocket', expect.anything())
  })

  it('leaves an unknown name as literal text — no image, no error UI, no fetch', async () => {
    setEmojiRegistry([{ name: 'rocket', etag: 'e1' }])
    const { container } = render(
      <RichTextEditor initialMarkdown={'This :notreal: stays text.\n'} editable={false} showToolbar={false} />,
    )
    // Give any (wrong) async rendering a chance to happen before asserting.
    await waitFor(() => expect(container.textContent).toContain(':notreal:'))
    expect(screen.queryByAltText(':notreal:')).not.toBeInTheDocument()
    expect(fetch).not.toHaveBeenCalled()
  })

  it('never decorates clock times: 10:30:45 has no registered name between its colons', async () => {
    setEmojiRegistry([{ name: 'rocket', etag: 'e1' }])
    const { container } = render(
      <RichTextEditor initialMarkdown={'Standup at 10:30:45 sharp.\n'} editable={false} showToolbar={false} />,
    )
    await waitFor(() => expect(container.textContent).toContain('10:30:45'))
    expect(container.querySelector('img.rw-emoji')).toBeNull()
  })

  it('re-decorates when the registry arrives after mount (the live page-load order)', async () => {
    // Registry empty at mount — the query hasn't answered yet.
    render(<RichTextEditor initialMarkdown={'Hello :wave:\n'} editable={false} showToolbar={false} />)
    expect(screen.queryByAltText(':wave:')).not.toBeInTheDocument()

    setEmojiRegistry([{ name: 'wave', etag: 'e2' }])
    expect(await screen.findByAltText(':wave:')).toBeInTheDocument()
  })

  it('does not decorate while editing — the author manipulates the literal source text', async () => {
    setEmojiRegistry([{ name: 'rocket', etag: 'e1' }])
    const { container } = render(
      <RichTextEditor initialMarkdown={'Launch :rocket: now.\n'} editable showToolbar={false} />,
    )
    await waitFor(() => expect(container.textContent).toContain(':rocket:'))
    expect(container.querySelector('img.rw-emoji')).toBeNull()
  })
})
