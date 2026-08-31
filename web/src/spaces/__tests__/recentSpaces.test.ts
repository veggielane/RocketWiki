import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  RECENT_SPACES_STORED_CAP,
  clearRecentSpaces,
  getRecentSpaceKeys,
  recordSpaceVisit,
  resetRecentSpacesCache,
  subscribeToRecentSpaces,
} from '../recentSpaces'

/**
 * The order half of "recent spaces". Membership and everything displayed comes
 * from the viewable space list instead — see the rail's own tests — so this
 * module deliberately knows nothing about whether a key still names a space
 * anybody may see.
 */
beforeEach(() => {
  window.localStorage.clear()
  resetRecentSpacesCache()
})

describe('recording a visit', () => {
  it('puts the space at the front', () => {
    recordSpaceVisit('ENG')
    recordSpaceVisit('OPS')
    expect(getRecentSpaceKeys()).toEqual(['OPS', 'ENG'])
  })

  it('moves a space already in the list rather than repeating it', () => {
    recordSpaceVisit('ENG')
    recordSpaceVisit('OPS')
    recordSpaceVisit('ENG')
    expect(getRecentSpaceKeys()).toEqual(['ENG', 'OPS'])
  })

  it('treats a space key case-insensitively, as the rest of the app does', () => {
    // /spaces/eng and /spaces/ENG are one space; storing both would spend two
    // of five slots on it and show it twice.
    recordSpaceVisit('ENG')
    recordSpaceVisit('eng')
    expect(getRecentSpaceKeys()).toEqual(['eng'])
  })

  it('caps what it keeps', () => {
    for (let i = 0; i < RECENT_SPACES_STORED_CAP + 5; i++) recordSpaceVisit(`S${i}`)
    expect(getRecentSpaceKeys()).toHaveLength(RECENT_SPACES_STORED_CAP)
    // The cap drops the OLDEST, not the newest.
    expect(getRecentSpaceKeys()[0]).toBe(`S${RECENT_SPACES_STORED_CAP + 4}`)
    expect(getRecentSpaceKeys()).not.toContain('S0')
  })

  it('ignores an empty or whitespace key rather than storing a blank row', () => {
    recordSpaceVisit('ENG')
    recordSpaceVisit('')
    recordSpaceVisit('   ')
    expect(getRecentSpaceKeys()).toEqual(['ENG'])
  })
})

describe('what the rail is told about', () => {
  it('notifies subscribers when the list changes', () => {
    const listener = vi.fn()
    subscribeToRecentSpaces(listener)
    recordSpaceVisit('ENG')
    expect(listener).toHaveBeenCalled()
  })

  it('stays silent when re-recording the space already at the front', () => {
    // Every navigation within one space records it again. Notifying each time
    // would re-render the rail on every page in a space, for no change.
    recordSpaceVisit('ENG')
    const listener = vi.fn()
    subscribeToRecentSpaces(listener)
    recordSpaceVisit('ENG')
    expect(listener).not.toHaveBeenCalled()
  })

  it('hands back the same array until something changes', () => {
    // `useSyncExternalStore` re-renders forever if the snapshot identity moves
    // on every read.
    recordSpaceVisit('ENG')
    expect(getRecentSpaceKeys()).toBe(getRecentSpaceKeys())
  })

  it('stops notifying an unsubscribed listener', () => {
    const listener = vi.fn()
    subscribeToRecentSpaces(listener)()
    recordSpaceVisit('ENG')
    expect(listener).not.toHaveBeenCalled()
  })
})

describe('it survives storage it cannot use', () => {
  it('reads nothing from a value that is not a list of keys', () => {
    // A convenience cache is worth less half-understood than empty.
    window.localStorage.setItem('rocketwiki:recent-spaces', '{"not":"an array"}')
    resetRecentSpacesCache()
    expect(getRecentSpaceKeys()).toEqual([])
  })

  it('reads nothing from unparseable storage', () => {
    window.localStorage.setItem('rocketwiki:recent-spaces', 'not json at all')
    resetRecentSpacesCache()
    expect(getRecentSpaceKeys()).toEqual([])
  })

  it('drops entries that are not strings', () => {
    window.localStorage.setItem('rocketwiki:recent-spaces', '["ENG", 42, null, "OPS"]')
    resetRecentSpacesCache()
    expect(getRecentSpaceKeys()).toEqual(['ENG', 'OPS'])
  })

  it('records without throwing when storage refuses to be written', () => {
    // Private modes and blocked third-party contexts throw outright. Failing to
    // remember must not fail a navigation.
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage disabled')
    })
    expect(() => recordSpaceVisit('ENG')).not.toThrow()
    // The in-memory copy still works for this session.
    expect(getRecentSpaceKeys()).toEqual(['ENG'])
    setItem.mockRestore()
  })
})

describe('signing out forgets where you have been', () => {
  it('clears the list and tells the rail', () => {
    recordSpaceVisit('ENG')
    const listener = vi.fn()
    subscribeToRecentSpaces(listener)

    clearRecentSpaces()

    expect(getRecentSpaceKeys()).toEqual([])
    expect(listener).toHaveBeenCalled()
  })

  it('leaves nothing behind in storage for the next person at that browser', () => {
    // A key like OPBLACKSTAR would otherwise tell whoever signs in next that
    // such a programme exists.
    recordSpaceVisit('OPBLACKSTAR')
    clearRecentSpaces()
    resetRecentSpacesCache()
    expect(window.localStorage.getItem('rocketwiki:recent-spaces')).toBeNull()
    expect(getRecentSpaceKeys()).toEqual([])
  })
})
