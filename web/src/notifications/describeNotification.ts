import type { NotificationPayload, NotificationType } from '../realtime/types'

const VERB_BY_TYPE: Record<NotificationType, string> = {
  page_watched_changed: 'updated',
  comment_reply: 'replied to your comment on',
  mention: 'mentioned you on',
  sync_bundle_landed: 'imported a sync bundle into',
}

export interface NotificationDescription {
  headline: string
  /** False whenever `pageTitle` is null — render as plain text, not a link, since there's nothing to navigate to that the recipient can open. */
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
 */
export function describeNotification(notification: NotificationPayload): NotificationDescription {
  const verb = VERB_BY_TYPE[notification.type]

  if (notification.pageTitle === null) {
    return {
      headline: `${notification.actorDisplayName} ${verb} a page you can no longer view`,
      linkable: false,
    }
  }

  return {
    headline: `${notification.actorDisplayName} ${verb} "${notification.pageTitle}"`,
    linkable: true,
  }
}
