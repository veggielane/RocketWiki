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
/// </summary>
public sealed record RestrictionCheckDetail(
    Guid RuleId,
    Guid PageId,
    PageAction Action,
    string ExpressionJson,
    bool Passed);

/// <summary>
/// design.md §6.6's "why can / can't user X see this page", as computed by
/// <see cref="EffectivePermissionCalculator.Explain"/>: the space-role computation,
/// the exact verdict the enforcement gate would reach (<see cref="Permission"/> —
/// same reasons, same precedence; a unit test pins Explain and Compute to identical
/// verdicts), and every restriction's individual pass/fail, not just the first
/// failing one.
/// </summary>
public sealed record EffectivePermissionExplanation(
    SpaceRole? SpaceRole,
    bool IsReplicaSpace,
    EffectivePermission Permission,
    IReadOnlyList<RestrictionCheckDetail> ViewRestrictions,
    IReadOnlyList<RestrictionCheckDetail> EditRestrictions);
