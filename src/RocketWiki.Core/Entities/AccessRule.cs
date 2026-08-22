using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Entities;

/// <summary>
/// data-model.md / design.md §6.4: one table, two kinds. SpaceGrant sets SpaceId + Role;
/// PageRestriction sets PageId + Action. ExpressionJson is a validated allOf/anyOf tree
/// over group/user/attr/everyone conditions — see RocketWiki.Core.Access.
/// </summary>
public class AccessRule
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public AccessRuleKind Kind { get; set; }

    /// <summary>Set iff Kind = SpaceGrant.</summary>
    public Guid? SpaceId { get; set; }
    public Space? Space { get; set; }

    /// <summary>Set iff Kind = PageRestriction.</summary>
    public Guid? PageId { get; set; }
    public Page? Page { get; set; }

    /// <summary>Grants: viewer/editor/space-admin. Set iff Kind = SpaceGrant.</summary>
    public SpaceRole? Role { get; set; }

    /// <summary>Restrictions: view/edit. Set iff Kind = PageRestriction.</summary>
    public PageAction? Action { get; set; }

    public string ExpressionJson { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public Guid UpdatedByUserId { get; set; }
}
