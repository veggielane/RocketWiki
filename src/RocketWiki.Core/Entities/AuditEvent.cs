using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Entities;

/// <summary>
/// data-model.md / design.md §7: AuditEvent — append-only. Clustered PK
/// (TimestampUtc, Id) aligned to monthly partitions in the SQL Server schema.
/// </summary>
public class AuditEvent
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }

    /// <summary>Null for system actors (e.g. sync CLI).</summary>
    public Guid? UserId { get; set; }
    public User? User { get; set; }

    /// <summary>e.g. "page.view", "sync.import".</summary>
    public string Action { get; set; } = string.Empty;

    public AuditSubjectType? SubjectType { get; set; }
    public Guid? SubjectId { get; set; }

    /// <summary>Denormalized for cheap filtering.</summary>
    public string? SpaceKey { get; set; }

    public AuditOutcome Outcome { get; set; }
    public AuditChannel Channel { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public string? McpClient { get; set; }

    /// <summary>Per-action payload: rule before/after, query text, failing restriction on a denial.</summary>
    public string? DetailsJson { get; set; }
}
