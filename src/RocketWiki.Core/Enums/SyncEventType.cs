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

    /// <summary>
    /// design.md §21: a page's protective marking changed. Markings travel with content —
    /// a page that is SECRET on low must not arrive on high as OFFICIAL, which is the
    /// whole reason this is a sync event type rather than an instance-local decision like
    /// a space grant.
    ///
    /// <para>Note the redundancy with <see cref="PageUpsert"/>, which also carries the
    /// page's current marking. That is deliberate: the upsert covers a page arriving or
    /// its content changing, and this covers a marking changing with no content edit
    /// behind it. Both apply idempotently to the same row, so an overlap is harmless
    /// and a gap would not be.</para>
    /// </summary>
    PageMarking = 10,
}
