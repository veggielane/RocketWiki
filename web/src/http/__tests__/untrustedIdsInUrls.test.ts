import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fetchAvatarBlob } from '../../avatars/avatarApi'
import { fetchAttachmentBlob, uploadAttachment } from '../../attachments/attachmentApi'

/**
 * Ids that reach a URL template are not typed by this app, and two of them come
 * from other users.
 *
 * - `fetchAvatarBlob`'s id is destructured from a remote peer's Yjs awareness
 *   payload (`editor/coedit/caretRender.ts`), which the hub relays opaquely
 *   without interpreting — so a co-editor chooses their own advertised id.
 * - `fetchAttachmentBlob`'s id is whatever follows `attachment://` in page
 *   Markdown (`editor/nodes/AttachmentImageView.tsx` slices the scheme off and
 *   passes the rest through), so a page AUTHOR chooses it and every reader's
 *   browser follows it.
 *
 * Both requests carry the caller's bearer token, so an unencoded id lets
 * somebody else pick the path a victim's authenticated GET goes to. The
 * response only ever becomes a `blob:` src and is never parsed, executed or
 * sent back to the attacker — which is why this is hardening rather than a
 * disclosure — but the request itself should never have been theirs to aim.
 */

/** A path that escapes its segment unless it is encoded. */
const TRAVERSAL = '../../users/victim/avatar'

let fetchMock: ReturnType<typeof vi.fn>
const realFetch = globalThis.fetch

beforeEach(() => {
  fetchMock = vi.fn(() => Promise.resolve(new Response(new Blob(['x']), { status: 200 })))
  globalThis.fetch = fetchMock as unknown as typeof fetch
})

afterEach(() => {
  globalThis.fetch = realFetch
})

/** The URL the last fetch was aimed at. */
const requestedUrl = () => String(fetchMock.mock.calls.at(-1)?.[0])

describe('an id chosen by another user cannot leave its path segment', () => {
  it('encodes the avatar user id', async () => {
    await fetchAvatarBlob(TRAVERSAL)
    expect(requestedUrl()).toBe('/users/..%2F..%2Fusers%2Fvictim%2Favatar/avatar')
    expect(requestedUrl()).not.toContain('/../')
  })

  it('encodes the attachment download id', async () => {
    await fetchAttachmentBlob(TRAVERSAL)
    expect(requestedUrl()).toBe('/attachments/..%2F..%2Fusers%2Fvictim%2Favatar')
    expect(requestedUrl()).not.toContain('/../')
  })

  it('encodes the upload page id too, so no id reaches a URL raw', async () => {
    // This one is route-derived rather than peer-supplied, so it is defence in
    // depth — but "every id goes through the same encoder" is the property
    // worth holding, not "the ones we currently believe are dangerous".
    fetchMock.mockResolvedValue(new Response('{}', { status: 200 }))
    await uploadAttachment(TRAVERSAL, new File(['x'], 'x.txt'))
    expect(requestedUrl()).not.toContain('/../')
  })

  it('leaves an ordinary id completely alone', async () => {
    // The ids these routes actually address are GUIDs and Keycloak subjects —
    // encoding must be invisible for every real value.
    const id = '5f2b6c1e-9b1e-4f0a-8f3d-2b7c1a4e6d90'
    await fetchAvatarBlob(id)
    expect(requestedUrl()).toBe(`/users/${id}/avatar`)
    await fetchAttachmentBlob(id)
    expect(requestedUrl()).toBe(`/attachments/${id}`)
  })

  it('neutralises a query-string suffix as well as a traversal', async () => {
    // `?` would otherwise start a query the route never intended to take.
    await fetchAttachmentBlob('abc?download=1')
    expect(requestedUrl()).toBe('/attachments/abc%3Fdownload%3D1')
  })
})
