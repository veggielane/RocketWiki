import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { getEmojiUrl, peekEmojiUrl, resetEmojiBlobCache } from '../emojiBlobCache'

/** jsdom implements neither createObjectURL nor revokeObjectURL — stubbed, same as useAttachmentBlobUrl.test. */
let urlCounter = 0
beforeEach(() => {
  urlCounter = 0
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(new Blob(['gif bytes']), { status: 200 })),
  )
  URL.createObjectURL = vi.fn(() => `blob:emoji-${++urlCounter}`)
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  resetEmojiBlobCache()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('emojiBlobCache — one object URL per (name, etag)', () => {
  it('fetches once for repeated requests with the same etag', async () => {
    const first = await getEmojiUrl('rocket', 'etag-1')
    const second = await getEmojiUrl('rocket', 'etag-1')
    expect(first).toBe('blob:emoji-1')
    expect(second).toBe(first)
    expect(fetch).toHaveBeenCalledTimes(1)
    expect(fetch).toHaveBeenCalledWith('/emojis/rocket', expect.anything())
  })

  it('busts the entry when the etag changes, revoking the stale object URL', async () => {
    const first = await getEmojiUrl('rocket', 'etag-1')
    const second = await getEmojiUrl('rocket', 'etag-2')
    expect(second).toBe('blob:emoji-2')
    expect(second).not.toBe(first)
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:emoji-1')
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('caches a miss as null — an unfetchable emoji is not re-probed per render', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 404 })),
    )
    expect(await getEmojiUrl('gone', 'etag-x')).toBeNull()
    expect(await getEmojiUrl('gone', 'etag-x')).toBeNull()
    expect(fetch).toHaveBeenCalledTimes(1)
    expect(peekEmojiUrl('gone', 'etag-x')).toBeNull()
  })

  it('peek reports undefined while pending or for a mismatched etag, then the URL', async () => {
    expect(peekEmojiUrl('rocket', 'etag-1')).toBeUndefined()
    const promise = getEmojiUrl('rocket', 'etag-1')
    expect(peekEmojiUrl('rocket', 'etag-1')).toBeUndefined() // in flight
    await promise
    expect(peekEmojiUrl('rocket', 'etag-1')).toBe('blob:emoji-1')
    expect(peekEmojiUrl('rocket', 'etag-other')).toBeUndefined()
  })

  it('reset revokes every held URL', async () => {
    await getEmojiUrl('a', '1')
    await getEmojiUrl('b', '2')
    resetEmojiBlobCache()
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:emoji-1')
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:emoji-2')
  })
})
