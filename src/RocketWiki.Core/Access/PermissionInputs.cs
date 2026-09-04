using RocketWiki.Core.Entities;

namespace RocketWiki.Core.Access;

/// <summary>
/// Everything <see cref="EffectivePermissionCalculator"/> needs to decide one page for one
/// principal (design.md §6.4 / §21.2), carried as one value so that no caller can hand the
/// calculator four of the five and quietly decide what the fifth is worth.
///
/// <para>Assembled by the loader in RocketWiki.Data (which owns the ordering and
/// fail-closed substitutions described on <c>PermissionContextLoader</c>) or, for the one
/// Api caller that cannot reach the loader, by hand in the same shape. Either way the
/// calculator sees the same five facts and applies the same ladder.</para>
/// </summary>
/// <param name="SpaceGrants">Every space-scoped grant of the page's space — access grants
/// (with their selector rows, §21.15) and role grants alike, one list from one query.
/// Restrictions present in the list are ignored, not an error.</param>
/// <param name="ChainRestrictions">Every page restriction attached to the page or any
/// ancestor, root-most ancestor first (§6.4: restrictions accumulate down the tree). The
/// order decides which failing restriction a denial names; see the loader's contract.</param>
/// <param name="IsReplicaSpace">See <see cref="Space.IsReplicaOf"/>. Only ever suppresses
/// <c>canEdit</c> (§12); view-only callers pass false deliberately.</param>
/// <param name="Marking">The page's own protective marking (§21). Required, never
/// nullable: a page with no marking row is <see cref="ProtectiveMarking.FailClosed"/>,
/// which denies everyone by its own flag, and that substitution is the loader's, not
/// the calculator's.</param>
/// <param name="Catalog">This instance's selector catalog (§21.15). A category the catalog
/// does not know can never be granted and is reported as unknown;
/// <see cref="SelectorCatalog.Empty"/> is therefore the fail-closed value, not a way to
/// skip the selector gate.</param>
public sealed record PermissionInputs(
    IReadOnlyList<AccessRule> SpaceGrants,
    IReadOnlyList<AccessRule> ChainRestrictions,
    bool IsReplicaSpace,
    ProtectiveMarking Marking,
    SelectorCatalog Catalog);
