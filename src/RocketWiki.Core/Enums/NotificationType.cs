namespace RocketWiki.Core.Enums;

/// <summary>data-model.md: Notification.Type.</summary>
public enum NotificationType : byte
{
    PageUpdated = 1,
    CommentReply = 2,
    Mention = 3,
    SyncImported = 4,
}
