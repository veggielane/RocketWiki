import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { getAvatarUrl, invalidateAvatar, peekAvatarUrl, resetAvatarCache } from '../avatarCache'

let urlCounter = 0
beforeEach(() => {
  urlCounter = 0
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(new Blob(['png bytes']), { status: 200 })),
  )
  URL.createObjectURL = vi.fn(() => `blob:avatar-${++urlCounter}`)
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  resetAvatarCache()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('avatarCache — one fetch per user per session', () => {
  it('dedupes concurrent and repeated requests for the same user', async () => {
    const [a, b] = await Promise.all([getAvatarUrl('user-1'), getAvatarUrl('user-1')])
    const c = await getAvatarUrl('user-1')
    expect(a).toBe('blob:avatar-1')
    expect(b).toBe(a)
    expect(c).toBe(a)
    expect(fetch).toHaveBeenCalledTimes(1)
    expect(fetch).toHaveBeenCalledWith('/users/user-1/avatar', expect.anything())
  })

  it('caches a 404 as null so a userId without an avatar is never re-probed', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 404 })),
    )
    expect(await getAvatarUrl('user-2')).toBeNull()
    expect(await getAvatarUrl('user-2')).toBeNull()
    expect(fetch).toHaveBeenCalledTimes(1)
    expect(peekAvatarUrl('user-2')).toBeNull()
  })

  it('invalidate revokes the object URL and the next request refetches', async () => {
    const first = await getAvatarUrl('user-1')
    invalidateAvatar('user-1')
    expect(URL.revokeObjectURL).toHaveBeenCalledWith(first)
    const second = await getAvatarUrl('user-1')
    expect(second).toBe('blob:avatar-2')
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('peek is undefined for unknown users, then the resolved URL', async () => {
    expect(peekAvatarUrl('user-1')).toBeUndefined()
    await getAvatarUrl('user-1')
    expect(peekAvatarUrl('user-1')).toBe('blob:avatar-1')
  })
})
