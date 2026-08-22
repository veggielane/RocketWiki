using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Entities;

/// <summary>
/// data-model.md: Notification — instance-local, never synced. Rows exist only for
/// recipients who passed canView at send time (design.md §8) — the row is created
/// after the permission check, not filtered on read.
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

    /// <summary>Page title as permitted at send time.</summary>
    public string? TitleSnapshot { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}
