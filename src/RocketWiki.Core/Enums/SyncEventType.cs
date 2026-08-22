namespace RocketWiki.Core.Enums;

/// <summary>data-model.md: SyncOutboxEvent.EventType.</summary>
public enum SyncEventType : byte
{
    PageUpsert = 1,
    PageMove = 2,
    PageDelete = 3,
    PageRestore = 4,
    Comment = 5,
    Attachment = 6,
    Labels = 7,
    Restrictions = 8,
}
