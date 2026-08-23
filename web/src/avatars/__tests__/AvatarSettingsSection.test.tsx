import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { AvatarSettingsSection } from '../AvatarSettingsSection'
import { resetAvatarCache } from '../avatarCache'

/**
 * The Settings profile-picture flow (design.md §19): choose → preview →
 * upload the ORIGINAL file (the server is the cropper), clear via DELETE,
 * and the 413 refusal rendered as designed copy carrying the server's own
 * `maxSizeBytes`, never a raw error toast.
 */

const PNG = new File(['png-bytes'], 'me.png', { type: 'image/png' })

function stubFetch(handler: (url: string, init?: RequestInit) => Response | Promise<Response>) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => handler(url, init)),
  )
}

beforeEach(() => {
  URL.createObjectURL = vi.fn(() => 'blob:preview-url')
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  resetAvatarCache()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function renderSection({ hasAvatar = false, onChanged = vi.fn() } = {}) {
  render(
    <AvatarSettingsSection localUserId="user-1" hasAvatar={hasAvatar} displayName="Ada Lovelace" onChanged={onChanged} />,
  )
  return { onChanged }
}

const chooseFile = (file: File) => {
  fireEvent.change(screen.getByLabelText('Choose profile picture'), { target: { files: [file] } })
}

describe('AvatarSettingsSection — upload flow', () => {
  it('previews the chosen file, uploads the original bytes, and reports success', async () => {
    stubFetch(() => new Response(JSON.stringify({ hasAvatar: true }), { status: 200 }))
    const { onChanged } = renderSection()

    chooseFile(PNG)
    expect(screen.getByRole('img', { name: 'New profile picture preview' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Upload' }))
    expect(await screen.findByText('Profile picture updated.')).toBeInTheDocument()

    const [url, init] = (fetch as ReturnType<typeof vi.fn>).mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/avatars')
    expect(init.method).toBe('POST')
    expect(init.body).toBeInstanceOf(FormData)
    expect((init.body as FormData).get('file')).toBe(PNG) // original file — server normalizes
    expect(onChanged).toHaveBeenCalled()
  })

  it('renders the 413 as designed copy carrying the server-declared cap', async () => {
    stubFetch(
      () =>
        new Response(
          JSON.stringify({ title: 'Avatar too large', status: 413, maxSizeBytes: 5242880 }),
          { status: 413 },
        ),
    )
    renderSection()
    chooseFile(PNG)
    fireEvent.click(screen.getByRole('button', { name: 'Upload' }))
    expect(await screen.findByText('That image is too big — avatars can be up to 5.2 MB.')).toBeInTheDocument()
  })

  it('refuses a non-image type client-side with designed copy, without a request', () => {
    stubFetch(() => new Response(null, { status: 500 }))
    renderSection()
    chooseFile(new File(['<svg/>'], 'sneaky.svg', { type: 'image/svg+xml' }))
    expect(screen.getByText('Avatars can be PNG, JPEG, or WebP images.')).toBeInTheDocument()
    expect(fetch).not.toHaveBeenCalled()
  })
})

describe('AvatarSettingsSection — clear flow', () => {
  it('DELETEs and reports removal', async () => {
    stubFetch(() => new Response(JSON.stringify({ hasAvatar: false }), { status: 200 }))
    const { onChanged } = renderSection({ hasAvatar: true })

    fireEvent.click(screen.getByRole('button', { name: 'Remove picture' }))
    expect(await screen.findByText('Profile picture removed.')).toBeInTheDocument()

    const [url, init] = (fetch as ReturnType<typeof vi.fn>).mock.calls.at(-1) as [string, RequestInit]
    expect(url).toBe('/avatars')
    expect(init.method).toBe('DELETE')
    expect(onChanged).toHaveBeenCalled()
  })

  it('offers no remove button without an avatar', () => {
    stubFetch(() => new Response(null, { status: 500 }))
    renderSection({ hasAvatar: false })
    expect(screen.queryByRole('button', { name: 'Remove picture' })).not.toBeInTheDocument()
  })

  it('cancelling a pending choice revokes the preview URL and restores the current face', async () => {
    stubFetch(() => new Response(new Blob(['png']), { status: 200 }))
    renderSection({ hasAvatar: true })
    chooseFile(PNG)
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:preview-url')
    await waitFor(() =>
      expect(screen.queryByRole('img', { name: 'New profile picture preview' })).not.toBeInTheDocument(),
    )
  })
})
