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

    /// <summary>
    /// design.md §20: a page property was set or removed. The wire format is this
    /// member's NAME, so the numeric value only has to stay stable within one instance's
    /// own outbox rows.
    /// </summary>
    PageProperties = 9,
}
