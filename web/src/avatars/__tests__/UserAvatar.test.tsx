import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import { UserAvatar } from '../UserAvatar'
import { resetAvatarCache } from '../avatarCache'
import { colourForUser } from '../../presence/colourForUser'

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(new Blob(['png bytes']), { status: 200 })),
  )
  URL.createObjectURL = vi.fn(() => 'blob:avatar-url')
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  resetAvatarCache()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('UserAvatar — hasAvatar decides, never a probing GET (design.md §19)', () => {
  it('renders initials without fetching when hasAvatar is false', () => {
    render(<UserAvatar userId="user-1" hasAvatar={false} displayName="Ada Lovelace" />)
    expect(screen.getByText('A')).toBeInTheDocument()
    expect(fetch).not.toHaveBeenCalled()
  })

  it('fetches through the blob cache and renders the image when hasAvatar is true', async () => {
    render(<UserAvatar userId="user-1" hasAvatar displayName="Ada Lovelace" />)
    const img = await screen.findByRole('img', { name: 'Ada Lovelace' })
    expect(img).toHaveAttribute('src', 'blob:avatar-url')
    expect(fetch).toHaveBeenCalledWith('/users/user-1/avatar', expect.anything())
  })

  it('falls back to initials when the fetch 404s (the presence case: no hasAvatar flag on the wire)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 404 })),
    )
    render(<UserAvatar userId="user-2" displayName="Grace Hopper" />)
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(1))
    expect(screen.getByText('G')).toBeInTheDocument()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })

  it('one fetch per user, however many mounts (comment rows share the session cache)', async () => {
    render(
      <>
        <UserAvatar userId="user-1" hasAvatar displayName="Ada Lovelace" />
        <UserAvatar userId="user-1" hasAvatar displayName="Ada Lovelace" />
        <UserAvatar userId="user-1" hasAvatar displayName="Ada Lovelace" />
      </>,
    )
    await screen.findAllByRole('img')
    expect(fetch).toHaveBeenCalledTimes(1)
  })

  it('initials fall back to colourForUser exactly as before avatars existed', () => {
    render(<UserAvatar userId="user-1" hasAvatar={false} displayName="Ada Lovelace" />)
    const avatar = screen.getByText('A').closest('.MuiAvatar-root') as HTMLElement
    expect(avatar.style.backgroundColor || getComputedStyle(avatar).backgroundColor).toBeTruthy()
    expect(colourForUser('user-1')).toMatch(/^hsl\(/)
  })

  it('a server-assigned presence colour overrides the derived one', () => {
    render(<UserAvatar userId="user-1" hasAvatar={false} displayName="Ada" colour="rgb(1, 2, 3)" />)
    const avatar = screen.getByText('A').closest('.MuiAvatar-root') as HTMLElement
    expect(getComputedStyle(avatar).backgroundColor).toBe('rgb(1, 2, 3)')
  })

  it('renders initials for a missing userId (shadow users) without fetching', () => {
    render(<UserAvatar userId={undefined} displayName="Shadow User" />)
    expect(screen.getByText('S')).toBeInTheDocument()
    expect(fetch).not.toHaveBeenCalled()
  })
})
