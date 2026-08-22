namespace RocketWiki.Core.Entities;

/// <summary>data-model.md: Space — top-level container.</summary>
public class Space
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? HomepageId { get; set; }
    public Page? Homepage { get; set; }

    /// <summary>Local InstanceId for native spaces; anything else ⇒ replica (read-only).</summary>
    public string OriginInstanceId { get; set; } = string.Empty;

    public bool IsExported { get; set; }
    public long LastOutboxSequence { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public Guid CreatedByUserId { get; set; }

    public ICollection<Page> Pages { get; set; } = new List<Page>();
    public ICollection<AccessRule> AccessRules { get; set; } = new List<AccessRule>();
    public ICollection<Label> Labels { get; set; } = new List<Label>();

    /// <summary>
    /// design.md §6.4 / §12: a space is a replica when its origin is not this instance.
    /// Replicas are unconditionally read-only regardless of any grant.
    /// </summary>
    public bool IsReplicaOf(string localInstanceId) =>
        !string.Equals(OriginInstanceId, localInstanceId, StringComparison.Ordinal);
}
