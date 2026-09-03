using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Access;

/// <summary>
/// One restriction, evaluated for the inspected principal, in the page-plus-ancestors
/// chain (design.md §6.4: restrictions accumulate down the tree). Unlike the
/// enforcement gate's <see cref="PermissionCheckResult"/> — which stops at the first
/// failure — every restriction in an explanation carries its own pass/fail, which is
/// the whole point of §6.6's inspector. <paramref name="PageId"/> is the chain page
/// the rule is attached to (not necessarily the inspected page); titles are a
/// storage-layer concern and are attached by the service that loads the chain.
///
/// <para>Since the calculator became one gate ladder (§6.4/§21.2), this is a
/// <i>projection</i> of the restriction entries in <see cref="EffectivePermissionExplanation.ViewGates"/>
/// and <see cref="EffectivePermissionExplanation.EditGates"/> rather than a separately
/// computed list — the two cannot disagree because there is only one evaluation.</para>
/// </summary>
public sealed record RestrictionCheckDetail(
    Guid RuleId,
    Guid PageId,
    PageAction Action,
    string ExpressionJson,
    bool Passed)
{
    /// <summary>The projection from a restriction gate check. Only meaningful for
    /// <see cref="GateKind.ViewRestriction"/> / <see cref="GateKind.EditRestriction"/>
    /// entries, which always carry the four rule members.</summary>
    internal static RestrictionCheckDetail From(GateCheck check) => new(
        check.RuleId ?? Guid.Empty,
        check.PageId ?? Guid.Empty,
        check.Action ?? PageAction.View,
        check.ExpressionJson ?? string.Empty,
        check.Passed);
}

/// <summary>
/// design.md §6.6's "why can / can't user X see this page", as computed by
/// <see cref="EffectivePermissionCalculator.Explain"/>: the space access and role
/// computations, the exact verdict the enforcement gate would reach
/// (<see cref="Permission"/> — same reasons, same precedence; a unit test pins Explain
/// and Compute to identical verdicts), and <b>every gate</b> evaluated, not just the first
/// failing one — S, C, E, G, N and each view restriction on the view side; replica, role
/// and each edit restriction on the edit side (§21.2's order).
///
/// <para><b>Explain never short-circuits.</b> When space access fails the marking gates
/// and restrictions are still evaluated (with an empty granted-selector set, so every
/// selector fails G) and listed here, because the inspector's whole purpose is the
/// complete picture. What a caller <i>without</i> space access may be told about the
/// marking is a separate question the API answers (§21.8, §6.7) — and
/// <see cref="MarkingWithheld"/> is the datum it acts on: a page whose space the
/// principal cannot enter discloses nothing beyond that fact, since the C/E/G/N tokens
/// embed the level and category names.</para>
/// </summary>
/// <param name="HasSpaceAccess">S: some access grant matched (§6.4). Roles never
/// supersede access — a role grant alone leaves this false.</param>
/// <param name="GrantedSelectors">The union over matching access grants (§21.15); empty
/// when none matched.</param>
/// <param name="SpaceRole">The highest matching role grant, or null. Confers no
/// visibility; decides the edit ladder's role gate.</param>
/// <param name="IsReplicaSpace">The replica invariant's input (§12).</param>
/// <param name="Permission">The verdict, derived from the gates below with exactly the
/// enforcement gate's precedence.</param>
/// <param name="ViewGates">S, C, E (per selector), G (per selector), N, then every view
/// restriction root-most first. Every entry evaluated.</param>
/// <param name="EditGates">Replica, role, then every edit restriction root-most first.
/// Every entry evaluated.</param>
public sealed record EffectivePermissionExplanation(
    bool HasSpaceAccess,
    IReadOnlySet<SelectorValue> GrantedSelectors,
    SpaceRole? SpaceRole,
    bool IsReplicaSpace,
    EffectivePermission Permission,
    IReadOnlyList<GateCheck> ViewGates,
    IReadOnlyList<GateCheck> EditGates)
{
    /// <summary>The view restrictions alone, in chain order — the shape the §6.6 inspector
    /// renders with chain-page titles attached. A projection of <see cref="ViewGates"/>.</summary>
    public IReadOnlyList<RestrictionCheckDetail> ViewRestrictions =>
        ViewGates.Where(g => g.Kind == GateKind.ViewRestriction).Select(RestrictionCheckDetail.From).ToList();

    /// <summary>The edit restrictions alone, in chain order. A projection of <see cref="EditGates"/>.</summary>
    public IReadOnlyList<RestrictionCheckDetail> EditRestrictions =>
        EditGates.Where(g => g.Kind == GateKind.EditRestriction).Select(RestrictionCheckDetail.From).ToList();

    /// <summary>
    /// design.md §6.7 / §21.8: a principal with no access grant in the space is told
    /// exactly that and nothing about the page's marking — not its level, not its
    /// selectors, not its caveat. The gates are still listed here for the inspector; this
    /// flag is what the API consults before rendering any of them to the principal
    /// themselves.
    /// </summary>
    public bool MarkingWithheld => !HasSpaceAccess;
}
