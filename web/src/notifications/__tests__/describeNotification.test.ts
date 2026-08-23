import { describe, expect, it } from 'vitest'
import { describeNotification } from '../describeNotification'
import type { NotificationPayload } from '../../realtime/types'

function notification(overrides: Partial<NotificationPayload> = {}): NotificationPayload {
  return {
    id: 'n1',
    type: 'mention',
    pageId: 'page-1',
    spaceKey: 'ENG',
    pageTitle: 'Runbook',
    actorDisplayName: 'Ada Lovelace',
    timestampUtc: '2026-01-01T00:00:00Z',
    readAtUtc: null,
    ...overrides,
  }
}

describe('describeNotification', () => {
  it('is linkable and names the page when the title is present', () => {
    const result = describeNotification(notification())
    expect(result.linkable).toBe(true)
    expect(result.headline).toContain('Runbook')
    expect(result.headline).toContain('Ada Lovelace')
  })

  it('is not linkable and never names the page when the title is absent', () => {
    const result = describeNotification(notification({ pageTitle: null }))
    expect(result.linkable).toBe(false)
    expect(result.headline).not.toMatch(/runbook/i)
  })

  it('never renders an error-sounding phrase for an absent title — it is a routine, not exceptional, state', () => {
    const result = describeNotification(notification({ pageTitle: null }))
    expect(result.headline).not.toMatch(/error|unavailable|failed/i)
  })

  it.each([
    ['page_watched_changed', /updated/i],
    ['comment_reply', /replied/i],
    ['mention', /mentioned/i],
    ['sync_bundle_landed', /imported|sync/i],
  ] as const)('phrases %s distinctly', (type, expectedWord) => {
    const result = describeNotification(notification({ type }))
    expect(result.headline).toMatch(expectedWord)
  })

  it('is not linkable when there is no page id to navigate to, even with a title', () => {
    const result = describeNotification(notification({ pageId: null }))
    expect(result.linkable).toBe(false)
  })

  describe('sync_bundle_landed (space-scoped rows: pageId null, spaceKey set)', () => {
    it('renders as a space-level message, not the lost-access copy — nothing was hidden, the row just is not about one page', () => {
      const result = describeNotification(
        notification({ type: 'sync_bundle_landed', pageId: null, pageTitle: null, spaceKey: 'ENG', actorDisplayName: null }),
      )
      expect(result.linkable).toBe(false)
      expect(result.headline).toContain('ENG')
      expect(result.headline).toMatch(/sync bundle/i)
      expect(result.headline).not.toMatch(/no longer view/i)
    })

    it('keeps the lost-access copy for a PAGE-scoped sync row whose title was nulled by canView', () => {
      const result = describeNotification(
        notification({ type: 'sync_bundle_landed', pageId: 'page-1', pageTitle: null, actorDisplayName: null }),
      )
      expect(result.linkable).toBe(false)
      expect(result.headline).toMatch(/no longer view/i)
    })

    it('renders actor-less — sync rows carry no actorDisplayName (the importer is a process, not a person)', () => {
      const result = describeNotification(
        notification({ type: 'sync_bundle_landed', pageId: null, pageTitle: null, spaceKey: 'ENG', actorDisplayName: null }),
      )
      expect(result.headline).not.toMatch(/null|undefined|someone/i)
    })

    it('names the page and stays linkable for a page-scoped sync row the recipient can still view', () => {
      const result = describeNotification(
        notification({ type: 'sync_bundle_landed', pageTitle: 'Runbook', actorDisplayName: null }),
      )
      expect(result.linkable).toBe(true)
      expect(result.headline).toContain('Runbook')
    })
  })

  it('falls back to an actor-less phrasing when actorDisplayName is null on a non-sync row', () => {
    const result = describeNotification(notification({ actorDisplayName: null }))
    expect(result.headline).not.toMatch(/null|undefined/)
    expect(result.headline).toContain('Runbook')
  })
})
