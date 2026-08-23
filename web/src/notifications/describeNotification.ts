import type { NotificationPayload, NotificationType } from '../realtime/types'

const VERB_BY_TYPE: Record<Exclude<NotificationType, 'sync_bundle_landed'>, string> = {
  page_watched_changed: 'updated',
  comment_reply: 'replied to your comment on',
  mention: 'mentioned you on',
}

export interface NotificationDescription {
  headline: string
  /** False whenever `pageTitle` (or the page id to navigate to) is null — render as plain text, not a link, since there's nothing to navigate to that the recipient can open. */
  linkable: boolean
}

/**
 * The one place a `NotificationPayload` becomes UI copy — every caller
 * goes through this rather than reaching into `pageTitle` directly, so
 * "what happens when the title is absent" only has to be gotten right
 * once. A null title isn't an error state (design.md §8: canView is
 * evaluated fresh per recipient at send time, so it's routine for a
 * restriction to have changed since), so this never says anything like
 * "unavailable" or "error" — just describes the page the recipient can
 * currently identify it by, which is none at all.
 *
 * `sync_bundle_landed` is the exception in two ways: it can be
 * SPACE-scoped (`pageId` null with `spaceKey` set — nothing was hidden,
 * the row just isn't about one page, so the lost-access copy would be
 * wrong), and its rows arrive actor-less (`actorDisplayName` null — the
 * importer is a process, not a person), so its copy never names an actor.
 */
export function describeNotification(notification: NotificationPayload): NotificationDescription {
  if (notification.type === 'sync_bundle_landed') {
    if (notification.pageId === null) {
      return {
        headline: notification.spaceKey
          ? `A sync bundle updated space "${notification.spaceKey}"`
          : 'A sync bundle updated a space you can no longer view',
        linkable: false,
      }
    }
    if (notification.pageTitle === null) {
      return { headline: 'A sync bundle updated a page you can no longer view', linkable: false }
    }
    return { headline: `A sync bundle updated "${notification.pageTitle}"`, linkable: true }
  }

  const verb = VERB_BY_TYPE[notification.type]
  const actor = notification.actorDisplayName ?? 'Someone'

  if (notification.pageTitle === null) {
    return {
      headline: `${actor} ${verb} a page you can no longer view`,
      linkable: false,
    }
  }

  return {
    headline: `${actor} ${verb} "${notification.pageTitle}"`,
    linkable: notification.pageId !== null,
  }
}
