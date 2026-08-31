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

    /// <summary>
    /// The person accountable for this space (design.md §6.5 — governance metadata).
    ///
    /// <para><b>This confers no access whatsoever.</b> Access on this system is the ABAC
    /// grants and nothing else, computed by the rule engine with no side-channel (§6.5's
    /// "no admin bypass"). An owner who should also administer the space needs an explicit
    /// space-admin grant, exactly like anyone else; ownership is who to hold responsible,
    /// not a permission. Any future code that reads this field to decide what somebody may
    /// do is a bypass of the grant model and is wrong by construction.</para>
    ///
    /// <para>Distinct from <see cref="CreatedByUserId"/>, which is immutable history —
    /// who first made the space. Ownership is reassignable and is meant to track who is
    /// currently responsible, which drifts from the creator as people move on.</para>
    ///
    /// <para><b>No foreign key</b>, matching <see cref="CreatedByUserId"/> beside it. That
    /// is not laxity: <c>BundleImportService</c> materialises a replica space with
    /// <c>CreatedByUserId = Guid.Empty</c> because users do not cross the sync boundary
    /// (§12 — each side has its own Keycloak), and an FK would make importing a bundle
    /// impossible. So this can legitimately hold an id no local User row matches, and
    /// <c>Space.owner</c> is nullable in the schema for exactly that reason.</para>
    /// </summary>
    public Guid OwnerUserId { get; set; }

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
