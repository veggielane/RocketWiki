using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Entities;

/// <summary>
/// data-model.md / design.md §6.4: one table, three kinds. A role grant
/// (<see cref="AccessRuleKind.RoleGrant"/>) sets SpaceId + Role; an access grant
/// (<see cref="AccessRuleKind.AccessGrant"/>) sets SpaceId alone and may carry
/// <see cref="Selectors"/>; a PageRestriction sets PageId + Action. ExpressionJson is a
/// validated allOf/anyOf tree over group/user/attr/everyone conditions — see
/// RocketWiki.Core.Access. Which columns each kind may populate is a check constraint
/// (<c>CK_AccessRules_KindColumnPairing</c>), not a convention.
/// </summary>
public class AccessRule
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public AccessRuleKind Kind { get; set; }

    /// <summary>Set iff Kind is a space-scoped grant (role grant or access grant).</summary>
    public Guid? SpaceId { get; set; }
    public Space? Space { get; set; }

    /// <summary>Set iff Kind = PageRestriction.</summary>
    public Guid? PageId { get; set; }
    public Page? Page { get; set; }

    /// <summary>Role grants: editor / space-admin. Set iff Kind = RoleGrant (the role grant
    /// kind); null on an access grant, which confers visibility and no role.</summary>
    public SpaceRole? Role { get; set; }

    /// <summary>Restrictions: view/edit. Set iff Kind = PageRestriction.</summary>
    public PageAction? Action { get; set; }

    public string ExpressionJson { get; set; } = string.Empty;

    /// <summary>
    /// The selector values this grant confers (design.md §21.15). Meaningful only on an
    /// access grant — the check constraint keeps a role grant's and a restriction's
    /// empty, and <c>AccessRuleService</c> refuses them there. Must be loaded
    /// (<c>Include</c>) wherever a grant feeds the calculator: a grant read without its
    /// selector rows confers none, which denies — the safe direction, but a wrong one.
    /// </summary>
    public ICollection<AccessRuleSelector> Selectors { get; set; } = new List<AccessRuleSelector>();

    public DateTime CreatedAtUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public Guid UpdatedByUserId { get; set; }

    /// <summary>The conferred selectors as canonical value objects, for the calculator's
    /// union and the audit snapshot. Canonicalizes on the way out, like
    /// <c>PageMarking.ToMarking</c>, so a hand-edited row still compares correctly.</summary>
    public IReadOnlySet<SelectorValue> SelectorValues() =>
        Selectors.Select(s => SelectorValue.Canonical(s.Category, s.Value)).ToHashSet();
}
