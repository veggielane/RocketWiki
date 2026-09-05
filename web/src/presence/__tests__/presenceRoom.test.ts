import { describe, expect, it } from 'vitest'
import { pageRoom, presenceRoomFor } from '../presenceRoom'

/**
 * Which room a route puts you in.
 *
 * You see the people on the same screen, so the room IS the screen — and "same
 * screen" has to mean "same layout", or "who else is here" would list people
 * looking at something else.
 */
describe('page screens name their own room', () => {
  // Their id is not always in the URL — the readable address carries a slug —
  // so this returns null and the screen supplies `page:{id}` once it knows it.
  // Guessing a path-shaped room in the meantime would put a reader and an
  // editor of one page in two different rooms, and would spend a join and a
  // leave on every page navigation.
  for (const path of [
    '/pages/abc-123',
    '/pages/abc-123/edit',
    '/pages/abc-123/history',
    '/pages/abc-123/details',
    '/pages/abc-123/permissions',
    '/spaces/ENG/runbook',
    '/spaces/ENG',
  ]) {
    it(`defers on ${path}`, () => {
      expect(presenceRoomFor(path)).toBeNull()
    })
  }

  it('gives one room per page id, not per URL', () => {
    expect(pageRoom('abc-123')).toBe('page:abc-123')
  })
})

describe('space screens share a room per space', () => {
  it('rooms the space-scoped screens by key and screen', () => {
    expect(presenceRoomFor('/spaces/ENG/-/browse')).toBe('space:ENG:browse')
    expect(presenceRoomFor('/spaces/ENG/-/grants')).toBe('space:ENG:grants')
    expect(presenceRoomFor('/spaces/ENG/-/trash')).toBe('space:ENG:trash')
  })

  it('does not put two different screens of one space in the same room', () => {
    // Same space, different layouts — someone reading the grants table is not
    // "here" on the trash list.
    expect(presenceRoomFor('/spaces/ENG/-/browse')).not.toBe(presenceRoomFor('/spaces/ENG/-/trash'))
  })

  it('treats a space key case-insensitively, as the rest of the app does', () => {
    // /spaces/eng and /spaces/ENG are the same screen; the server canonicalises
    // the key and `sameSpaceKey` is how the SPA compares them.
    expect(presenceRoomFor('/spaces/eng/-/browse')).toBe(presenceRoomFor('/spaces/ENG/-/browse'))
  })

  it('never reads a literal /spaces/ screen as a space key', () => {
    // `/spaces/new` would otherwise become the room `space:NEW`, and everyone
    // creating a space would join a room named after a space that exists.
    expect(presenceRoomFor('/spaces/new')).toBe('site:/spaces/new')
    expect(presenceRoomFor('/spaces/archived')).toBe('site:/spaces/archived')
  })
})

describe('every other screen gets a room of its own', () => {
  it('rooms the global screens by path', () => {
    expect(presenceRoomFor('/')).toBe('site:/')
    expect(presenceRoomFor('/search')).toBe('site:/search')
    expect(presenceRoomFor('/ask')).toBe('site:/ask')
    expect(presenceRoomFor('/settings')).toBe('site:/settings')
    expect(presenceRoomFor('/admin')).toBe('site:/admin')
    expect(presenceRoomFor('/admin/users')).toBe('site:/admin/users')
    expect(presenceRoomFor('/-/docs')).toBe('site:/-/docs')
    // The space list is a screen in its own right now, not the home route.
    expect(presenceRoomFor('/spaces')).toBe('site:/spaces')
  })

  it('keeps two admin screens apart', () => {
    expect(presenceRoomFor('/admin/users')).not.toBe(presenceRoomFor('/admin/audit'))
  })

  it('keeps two help topics apart, because they are different reading', () => {
    expect(presenceRoomFor('/-/docs/markdown')).not.toBe(presenceRoomFor('/-/docs/permissions'))
  })

  it('does not split a room on casing or a trailing slash', () => {
    expect(presenceRoomFor('/Search')).toBe(presenceRoomFor('/search'))
    expect(presenceRoomFor('/search/')).toBe(presenceRoomFor('/search'))
  })

  it('puts the search room in one place regardless of the query', () => {
    // The query lives in `?q=`, which is not part of the pathname — two people
    // searching different things are still looking at the same layout.
    expect(presenceRoomFor('/search')).toBe('site:/search')
  })
})
