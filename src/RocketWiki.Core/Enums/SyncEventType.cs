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

    /// <summary>
    /// <b>Retired. Reserved — this number must never be reused.</b>
    ///
    /// <para>Page entries (structured records stored against a page, with a form fence
    /// on top) were built and then removed; the design survives only as the
    /// ticket-creation front end sketched in docs/PLATFORM-PLAN.md, which stores nothing
    /// of its own. Nothing produces this event any more and the import side skips a
    /// line carrying it (<c>BundleImportService.ApplyEventAsync</c>) rather than
    /// refusing the bundle — a full-featured instance's outbox may still hold rows of
    /// this type, and its bundles must go on landing here.</para>
    ///
    /// <para>The member stays for two reasons. First, the wire format is this member's
    /// NAME and <c>ParseEventType</c> only accepts names the enum defines, so deleting
    /// it would turn every such bundle into a refusal. Second, and the reason the
    /// NUMBER is pinned by a test: <c>SyncOutboxEvent.EventType</c> is stored as this
    /// tinyint, and an instance that still holds entry rows in its outbox would read a
    /// re-issued 11 as whatever new meaning it was given. A reused wire number is how
    /// two instances silently corrupt each other. The next member is 12.</para>
    /// </summary>
    PageEntry = 11,
}
