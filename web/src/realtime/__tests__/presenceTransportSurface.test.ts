import { describe, expect, it } from 'vitest'
import { FakePresenceTransport } from '../FakePresenceTransport'
import { SignalRPresenceTransport } from '../SignalRPresenceTransport'
import type { PresenceTransport } from '../types'

/**
 * Live mouse pointers were removed from presence deliberately (the product
 * owner's call: not a serious feature). Presence is "who else is here" —
 * `joinRoom`/`leaveRoom`/`onViewersChanged` — and nothing more; text carets
 * belong to the co-editing layer, not to this interface.
 *
 * This pins the removal at the transport seam, where a re-addition would have
 * to start: neither implementation may grow a pointer member back, and the
 * interface may not either. Absence is otherwise the one thing no test sees.
 */

// Type-level half, checked by `npm run typecheck`: the interface's member
// names are a union of literals, `Extract` keeps the ones that mention a
// pointer, and only an empty union is assignable to `never`. A re-added
// `onPointerMoved` turns the annotation below into `false` and the `true`
// literal no longer fits.
type PointerMembers = Extract<keyof PresenceTransport, `${string}Pointer${string}` | `${string}pointer${string}`>
const interfaceHasNoPointerMember: [PointerMembers] extends [never] ? true : false = true

describe('the presence transport has no pointer surface', () => {
  it('declares no pointer member on the interface', () => {
    expect(interfaceHasNoPointerMember).toBe(true)
  })

  it.each([
    ['FakePresenceTransport', FakePresenceTransport.prototype as object],
    ['SignalRPresenceTransport', SignalRPresenceTransport.prototype as object],
  ])('%s implements no pointer member', (_name, prototype) => {
    const pointerMembers = Object.getOwnPropertyNames(prototype).filter((name) => /pointer/i.test(name))
    expect(pointerMembers).toEqual([])
  })

  it('the fake records no pointer traffic on an instance either', () => {
    // `sentPositions` was the fake's record of outbound pointer samples; a test
    // double that still keeps one is a feature half-removed.
    const fake = new FakePresenceTransport()
    const pointerState = Object.keys(fake).filter((name) => /pointer|position/i.test(name))
    expect(pointerState).toEqual([])
  })
})
