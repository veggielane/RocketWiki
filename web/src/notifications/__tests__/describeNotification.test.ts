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
})
