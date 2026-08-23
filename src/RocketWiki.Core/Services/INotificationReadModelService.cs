using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// The persisted notification list (design.md §8: "SignalR delivers the live nudge,
/// the table is the record") and its one mutation, markNotificationRead.
///
/// data-model.md's rendering rule is enforced here, in one place. Rows written after
/// a send-time canView (they carry a TitleSnapshot) still get canView re-checked per
/// row at read time — "a row written last week may name a page the user can no longer
/// see, and stale titles must not resurface." Such a row that fails the re-check is
/// still returned (the recipient legitimately learned of the event when it was sent)
/// with <see cref="NotificationListItem.PageTitle"/> null — the exact null-title
/// contract the frontend's describeNotification.ts renders as "a page you can no
/// longer view", absent rather than error. Rows written with NO send-time canView
/// (null TitleSnapshot: sync-imported rows from the offline CLI, and the dispatcher's
/// deferred rows for offline recipients) are existence-gated instead: the read-time
/// check is their only check, and a row that fails it is suppressed entirely, fail
/// closed — surviving rows carry the page's live title, which the caller could open
/// the page to read anyway.
/// </summary>
public interface INotificationReadModelService
{
    /// <summary>The recipient's own rows only, newest first, capped at <paramref name="take"/>.</summary>
    Task<IReadOnlyList<NotificationListItem>> GetNotificationsAsync(Guid recipientUserId, Principal principal, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scoped to the caller's own rows: a notification id belonging to another
    /// recipient is NotFound, indistinguishable from one that doesn't exist — a
    /// notification row's existence reveals a page's existence to its recipient only.
    /// Existence-gated rows (null TitleSnapshot) additionally require
    /// <paramref name="principal"/> to pass the same read-time check the list runs;
    /// a gated row that fails is the same NotFound and stays unread, so probing
    /// sequential ids reveals nothing the list would suppress.
    /// Idempotent: marking an already-read row returns it unchanged (no event).
    /// </summary>
    Task<PageMutationResult<NotificationListItem>> MarkNotificationReadAsync(long notificationId, Guid actingUserId, Principal principal, AuditContext auditContext, CancellationToken cancellationToken = default);
}

/// <summary>
/// One row of the persisted list, post canView re-check. Carries exactly what design.md
/// §8 allows a notification to reveal — type, page id, space key, actor display name,
/// timestamp, and the title only while the recipient can (still) view the page — never
/// content, never diffs.
/// </summary>
public sealed record NotificationListItem(
    long Id,
    NotificationType Type,
    Guid? PageId,
    string? SpaceKey,
    string? PageTitle,
    string ActorDisplayName,
    DateTime CreatedAtUtc,
    DateTime? ReadAtUtc);
