using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// What the current caller holds in one space (design.md §6.4 / §21.15), as the three
/// answers the two grant kinds give: the highest matching ROLE grant (what they may do),
/// whether any ACCESS grant admits them (what they may see), and the selector values
/// the matching access grants confer. <see cref="Role"/> is nullable and
/// <see cref="HasAccess"/> a bool deliberately: a loader yields <c>default(TValue)</c>
/// for a key it did not resolve, and every consumer must read "missing" as "nothing
/// held" — fail closed, never the role that happens to sit on <c>0</c>.
/// </summary>
public sealed record SpaceAccessFacts(SpaceRole? Role, bool HasAccess, IReadOnlyList<SelectorValue> GrantedSelectors)
{
    /// <summary>What an unresolved key is worth: no role, no access, no selectors.</summary>
    public static SpaceAccessFacts None { get; } = new(null, false, []);
}

/// <summary>
/// Batches "what does the current caller hold in this space" — one AccessRules query per
/// request rather than one per space (design.md §8's DataLoader rule) — behind
/// <c>Space.canManageAccess</c>, <c>Space.viewerHasAccess</c> and
/// <c>Space.viewerSelectorGrants</c>, all three of which are selected across a LIST: a
/// space listing renders a manage control and an access note per row, so a per-space
/// query would be an N+1 that grows with the number of spaces the caller can see.
/// Contrast <c>SpaceFieldResolvers.GetGrantsAsync</c>, which queries per space and is
/// fine doing so — it is asked for one space at a time, in a settings screen.
///
/// <para><b>Scoped to one principal, which is why the principal is a constructor
/// dependency rather than part of the key.</b> A DataLoader is per-request and so is
/// <see cref="ICurrentPrincipalAccessor"/>; folding the caller into the cache key
/// would imply this could serve two principals in one request, which is exactly the
/// confusion that turns a cache into a cross-user leak. It cannot, and the shape says
/// so.</para>
///
/// <para>Both answers come from the calculator — <see cref="EffectivePermissionCalculator.ComputeSpaceRole"/>
/// over the role grants, <see cref="EffectivePermissionCalculator.ComputeSpaceAccess"/>
/// over the access grants — evaluated against the TOKEN's principal, never the local
/// User mirror (§6.1), from one query that loads both kinds with their selector rows.
/// The two questions stay two answers on purpose: a role confers no visibility and
/// access confers no role (§6.4), and a consumer that wants "may manage" reads
/// <see cref="SpaceAccessFacts.Role"/>, never <see cref="SpaceAccessFacts.HasAccess"/>.</para>
/// </summary>
public sealed class SpaceAccessFactsBySpaceIdDataLoader(
    DbContextOptions<RocketWikiDbContext> dbOptions,
    ICurrentPrincipalAccessor principalAccessor,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : BatchDataLoader<Guid, SpaceAccessFacts>(batchScheduler, options ?? new DataLoaderOptions())
{
    protected override async Task<IReadOnlyDictionary<Guid, SpaceAccessFacts>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            // Anonymous holds nothing anywhere. An empty map means every key resolves
            // to "nothing held" — fail closed, no query needed.
            return new Dictionary<Guid, SpaceAccessFacts>();
        }

        // Own context per batch — see DataLoaderDbContext.
        await using var db = DataLoaderDbContext.Create(dbOptions);

        var grants = await db.AccessRules
            .Include(r => r.Selectors)
            .Where(r => (r.Kind == AccessRuleKind.RoleGrant || r.Kind == AccessRuleKind.AccessGrant)
                && keys.Contains(r.SpaceId!.Value))
            .ToListAsync(cancellationToken);

        var facts = new Dictionary<Guid, SpaceAccessFacts>();
        foreach (var group in grants.GroupBy(r => r.SpaceId!.Value))
        {
            var spaceGrants = group.ToList();
            var access = EffectivePermissionCalculator.ComputeSpaceAccess(spaceGrants, principal);
            var role = EffectivePermissionCalculator.ComputeSpaceRole(spaceGrants, principal);
            if (access is null && role is null)
            {
                // Absent rather than a "none" value when the caller holds nothing here:
                // the dictionary's own "missing key" IS the nothing-held answer.
                continue;
            }

            facts[group.Key] = new SpaceAccessFacts(
                role,
                access is not null,
                access is null
                    ? []
                    : access.GrantedSelectors.OrderBy(s => s, SelectorValue.CanonicalOrder).ToList());
        }

        return facts;
    }
}
