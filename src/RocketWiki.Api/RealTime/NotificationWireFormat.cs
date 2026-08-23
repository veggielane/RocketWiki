using RocketWiki.Core.Enums;

namespace RocketWiki.Api.RealTime;

/// <summary>
/// The one mapping from <see cref="NotificationType"/> to the snake_case wire
/// vocabulary the frontend shipped first (web/src/realtime/types.ts
/// <c>NotificationType</c>, rendered by web/src/notifications/describeNotification.ts).
/// Used identically by the SignalR push payload and the GraphQL <c>notifications</c>
/// list, so a live push and its later persisted fetch can never disagree on type.
/// </summary>
public static class NotificationWireFormat
{
    public static string TypeString(NotificationType type) => type switch
    {
        NotificationType.PageUpdated => "page_watched_changed",
        NotificationType.CommentReply => "comment_reply",
        NotificationType.Mention => "mention",
        NotificationType.SyncImported => "sync_bundle_landed",
        _ => throw new NotSupportedException(
            $"No wire mapping for NotificationType.{type} - the frontend's NotificationType union " +
            "(web/src/realtime/types.ts) must be extended in the same change as this enum."),
    };
}
