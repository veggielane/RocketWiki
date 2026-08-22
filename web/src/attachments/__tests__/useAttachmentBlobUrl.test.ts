import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { useAttachmentBlobUrl } from '../useAttachmentBlobUrl'

/**
 * jsdom doesn't implement `URL.createObjectURL`/`revokeObjectURL`, so
 * they're stubbed here rather than left to throw — that's an environment
 * gap, not something worth working around in the hook itself.
 */
beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(new Blob(['fake image bytes']), { status: 200 })),
  )
  URL.createObjectURL = vi.fn(() => 'blob:mock-url')
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('useAttachmentBlobUrl', () => {
  it('starts loading, then resolves to a ready blob url', async () => {
    const { result } = renderHook(() => useAttachmentBlobUrl('att-1'))
    expect(result.current.status).toBe('loading')

    await waitFor(() => expect(result.current.status).toBe('ready'))
    expect(result.current).toEqual({ status: 'ready', url: 'blob:mock-url' })
  })

  it('goes to the same error state on a failed fetch as on a denied one — the caller cannot distinguish them, by design', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 403 })),
    )
    const { result } = renderHook(() => useAttachmentBlobUrl('att-1'))
    await waitFor(() => expect(result.current.status).toBe('error'))
  })

  it('is in the error state immediately for a null id, without ever fetching', () => {
    const fetchSpy = vi.fn()
    vi.stubGlobal('fetch', fetchSpy)
    const { result } = renderHook(() => useAttachmentBlobUrl(null))
    expect(result.current.status).toBe('error')
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('revokes the object url on unmount', async () => {
    const { result, unmount } = renderHook(() => useAttachmentBlobUrl('att-1'))
    await waitFor(() => expect(result.current.status).toBe('ready'))

    unmount()
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:mock-url')
  })

  it('re-fetches and revokes the previous url when the id changes', async () => {
    const { result, rerender } = renderHook(({ id }) => useAttachmentBlobUrl(id), {
      initialProps: { id: 'att-1' },
    })
    await waitFor(() => expect(result.current.status).toBe('ready'))

    rerender({ id: 'att-2' })
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:mock-url')
    await waitFor(() => expect(result.current.status).toBe('ready'))
  })
})
