using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Entities;

/// <summary>
/// data-model.md: Notification — instance-local, never synced. Two write shapes,
/// discriminated by <see cref="TitleSnapshot"/> (design.md §8):
/// a row WITH a snapshot was written after a send-time canView against the
/// recipient's live Principal (the dispatcher, for connected recipients); a row with
/// a NULL snapshot was written where no live token existed to check against — by the
/// offline sync CLI (<see cref="NotificationType.SyncImported"/>) or by the
/// dispatcher for an offline recipient — so nothing was disclosed at write time and
/// the check is deferred: NotificationReadModelService suppresses the whole row
/// unless the recipient's live Principal passes at fetch time (fail closed either
/// way), serving the page's live title when it does.
/// </summary>
public class Notification
{
    public long Id { get; set; }
    public Guid RecipientUserId { get; set; }
    public User? Recipient { get; set; }
    public NotificationType Type { get; set; }
    public Guid? PageId { get; set; }
    public Page? Page { get; set; }
    public Guid? SpaceId { get; set; }
    public Space? Space { get; set; }

    /// <summary>Null for system events.</summary>
    public Guid? ActorUserId { get; set; }
    public User? Actor { get; set; }

    /// <summary>Page title as permitted at send time. Null when no principal existed
    /// at write time to authorize any disclosure (sync-imported and deferred-offline
    /// rows) — such rows are existence-gated at read; see the class doc.</summary>
    public string? TitleSnapshot { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}
