import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { EmojiImg } from '../EmojiImg'
import { resetEmojiBlobCache } from '../emojiBlobCache'

/** jsdom implements neither createObjectURL nor revokeObjectURL — stubbed, same as emojiBlobCache.test. */
let urlCounter = 0
beforeEach(() => {
  urlCounter = 0
  URL.createObjectURL = vi.fn(() => `blob:emoji-${++urlCounter}`)
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  resetEmojiBlobCache()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('EmojiImg', () => {
  it('renders the literal :name: text until the blob resolves, then the image', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(new Blob(['gif']), { status: 200 })),
    )
    render(<EmojiImg name="rocket" etag="etag-1" />)
    expect(screen.getByText(':rocket:')).toBeInTheDocument()
    const img = await screen.findByAltText(':rocket:')
    expect(img).toHaveAttribute('src', 'blob:emoji-1')
  })

  it('a name change resets to the literal text immediately — never a frame of the previous emoji', async () => {
    // rocket resolves; banana stays in flight forever, so anything shown
    // for banana synchronously after the prop change is the reset path.
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) =>
        String(input).includes('rocket')
          ? Promise.resolve(new Response(new Blob(['gif']), { status: 200 }))
          : new Promise<Response>(() => {}),
      ),
    )
    const view = render(<EmojiImg name="rocket" etag="etag-1" />)
    await screen.findByAltText(':rocket:')

    view.rerender(<EmojiImg name="banana" etag="etag-2" />)
    // The regression this pins: without the render-time reset, the state
    // still held rocket's blob URL and rendered it under banana's name.
    expect(screen.getByText(':banana:')).toBeInTheDocument()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })
})
