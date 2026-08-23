import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { AdminEmojisPage } from '../AdminEmojisPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { resetEmojiBlobCache } from '../../emoji/emojiBlobCache'

/**
 * Admin curation of the emoji registry (design.md §19). The list rides the
 * generated CustomEmojis query; the binary mutations are raw-fetch routes
 * (POST raw bytes / DELETE), so those are stubbed at the fetch layer with
 * the server's real error shapes (PageMutationErrorView JSON, ProblemDetails
 * 413) to prove the designed copy, 409 included.
 */

const GIF = new File(['gif-bytes'], 'party.gif', { type: 'image/gif' })

beforeEach(() => {
  URL.createObjectURL = vi.fn(() => 'blob:emoji-url')
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  resetEmojiBlobCache()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function stubMutationFetch(handler: (url: string, init?: RequestInit) => Response) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      if (!init?.method || init.method === 'GET') {
        // EmojiImg previews — serve bytes for any GET.
        return new Response(new Blob(['img']), { status: 200 })
      }
      return handler(url, init)
    }),
  )
}

function renderPage(initialNames = ['rocket', 'tada']) {
  let names = [...initialNames]
  const mock = createMockUrqlClient((name) => {
    if (name === 'CustomEmojis') {
      return { customEmojis: names.map((n) => ({ name: n, etag: `etag-${n}` })) }
    }
    return undefined
  })
  const utils = render(
    <UrqlProvider value={mock.client}>
      <AdminEmojisPage />
    </UrqlProvider>,
  )
  return { mock, setNames: (next: string[]) => (names = next), ...utils }
}

const typeName = (value: string) => {
  fireEvent.change(screen.getByLabelText('Name'), { target: { value } })
}

const chooseFile = (file: File) => {
  fireEvent.change(screen.getByLabelText('Choose emoji image'), { target: { files: [file] } })
}

describe('AdminEmojisPage — list', () => {
  it('lists the registry with previews and per-row delete affordances', async () => {
    stubMutationFetch(() => new Response(null, { status: 500 }))
    renderPage()
    expect((await screen.findAllByText(':rocket:')).length).toBeGreaterThan(0)
    expect(screen.getByRole('button', { name: 'Delete :rocket:' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Delete :tada:' })).toBeInTheDocument()
  })
})

describe('AdminEmojisPage — upload', () => {
  it('refuses a grammar-invalid name client-side and disables upload', async () => {
    stubMutationFetch(() => new Response(null, { status: 500 }))
    renderPage()
    await screen.findAllByText(':rocket:')
    typeName('Bad Name!')
    chooseFile(GIF)
    expect(screen.getByText(/Lowercase letters, digits/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Upload' })).toBeDisabled()
  })

  it('POSTs the raw bytes with the file content type, then refetches the list', async () => {
    stubMutationFetch(
      () =>
        new Response(
          JSON.stringify({ name: 'party', contentType: 'image/gif', sizeBytes: 9, pixelSize: 64, etag: '"abc"' }),
          { status: 201 },
        ),
    )
    const { mock, setNames } = renderPage()
    await screen.findAllByText(':rocket:')

    typeName('party')
    chooseFile(GIF)
    setNames(['party', 'rocket', 'tada'])
    fireEvent.click(screen.getByRole('button', { name: 'Upload' }))

    expect(await screen.findByText('Added :party:')).toBeInTheDocument()
    const mutationCall = (fetch as ReturnType<typeof vi.fn>).mock.calls.find(
      ([, init]) => (init as RequestInit | undefined)?.method === 'POST',
    ) as [string, RequestInit]
    expect(mutationCall[0]).toBe('/emojis/party')
    expect(mutationCall[1].body).toBe(GIF) // raw bytes, not multipart
    expect((mutationCall[1].headers as Record<string, string>)['Content-Type']).toBe('image/gif')
    // The list refetched (a second CustomEmojis execution) and shows the new row.
    await waitFor(() =>
      expect(mock.operations.filter((op) => op.name === 'CustomEmojis').length).toBeGreaterThanOrEqual(2),
    )
    expect((await screen.findAllByText(':party:')).length).toBeGreaterThan(0)
  })

  it('surfaces the 409 NameTaken with the server message', async () => {
    stubMutationFetch(
      () =>
        new Response(JSON.stringify({ kind: 'NameTaken', message: "An emoji named 'rocket' already exists." }), {
          status: 409,
        }),
    )
    renderPage()
    await screen.findAllByText(':rocket:')
    typeName('rocket')
    chooseFile(GIF)
    fireEvent.click(screen.getByRole('button', { name: 'Upload' }))
    expect(await screen.findByText("An emoji named 'rocket' already exists.")).toBeInTheDocument()
  })

  it('renders the 413 as designed copy carrying the server-declared cap', async () => {
    stubMutationFetch(
      () => new Response(JSON.stringify({ title: 'Emoji image too large', maxSizeBytes: 262144 }), { status: 413 }),
    )
    renderPage()
    await screen.findAllByText(':rocket:')
    typeName('party')
    chooseFile(GIF)
    fireEvent.click(screen.getByRole('button', { name: 'Upload' }))
    expect(await screen.findByText('That image is too big — emoji uploads can be up to 262 KB.')).toBeInTheDocument()
  })

  it('surfaces a Validation refusal (server image checks) verbatim', async () => {
    stubMutationFetch(
      () =>
        new Response(JSON.stringify({ kind: 'Validation', message: 'Image aspect ratio exceeds 2:1.' }), {
          status: 400,
        }),
    )
    renderPage()
    await screen.findAllByText(':rocket:')
    typeName('wide')
    chooseFile(GIF)
    fireEvent.click(screen.getByRole('button', { name: 'Upload' }))
    expect(await screen.findByText('Image aspect ratio exceeds 2:1.')).toBeInTheDocument()
  })
})

describe('AdminEmojisPage — delete', () => {
  it('confirms, DELETEs, and refetches', async () => {
    stubMutationFetch(() => new Response(null, { status: 204 }))
    const { mock, setNames } = renderPage()
    await screen.findAllByText(':rocket:')

    fireEvent.click(screen.getByRole('button', { name: 'Delete :rocket:' }))
    expect(await screen.findByText('Delete :rocket:?')).toBeInTheDocument()
    setNames(['tada'])
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }))

    expect(await screen.findByText(/Deleted :rocket:/)).toBeInTheDocument()
    const deleteCall = (fetch as ReturnType<typeof vi.fn>).mock.calls.find(
      ([, init]) => (init as RequestInit | undefined)?.method === 'DELETE',
    ) as [string, RequestInit]
    expect(deleteCall[0]).toBe('/emojis/rocket')
    await waitFor(() =>
      expect(mock.operations.filter((op) => op.name === 'CustomEmojis').length).toBeGreaterThanOrEqual(2),
    )
  })

  it('cancel closes the dialog without a request', async () => {
    stubMutationFetch(() => new Response(null, { status: 204 }))
    renderPage()
    await screen.findAllByText(':rocket:')
    fireEvent.click(screen.getByRole('button', { name: 'Delete :rocket:' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }))
    const deleteCalls = (fetch as ReturnType<typeof vi.fn>).mock.calls.filter(
      ([, init]) => (init as RequestInit | undefined)?.method === 'DELETE',
    )
    expect(deleteCalls).toHaveLength(0)
    await waitFor(() => expect(screen.queryByText('Delete :rocket:?')).not.toBeInTheDocument())
  })
})
