using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Batches "what role does the current caller hold in this space" — one AccessRules
/// query per request rather than one per space (design.md §8's DataLoader rule).
///
/// <para>This exists because <c>Space.canManageAccess</c> is selected across a
/// LIST: a space listing renders a manage control per row, so a per-space query would
/// be an N+1 that grows with the number of spaces the caller can see. Contrast
/// <c>SpaceFieldResolvers.GetGrantsAsync</c>, which queries per space and is fine
/// doing so — it is asked for one space at a time, in a settings screen.</para>
///
/// <para><b>Scoped to one principal, which is why the principal is a constructor
/// dependency rather than part of the key.</b> A DataLoader is per-request and so is
/// <see cref="ICurrentPrincipalAccessor"/>; folding the caller into the cache key
/// would imply this could serve two principals in one request, which is exactly the
/// confusion that turns a cache into a cross-user leak. It cannot, and the shape says
/// so.</para>
///
/// <para>Roles come from <see cref="EffectivePermissionCalculator.ComputeSpaceRole"/>
/// — the same computation every other path uses — evaluated against the TOKEN's
/// principal, never the local User mirror (§6.1). A space with no grant for this
/// caller is absent from the result, which callers must read as "no role" (fail
/// closed), the same contract <c>IPagePermissionReadService</c> states.</para>
///
/// <para><b>The value type is <c>SpaceRole?</c> deliberately.</b> A DataLoader yields
/// <c>default(TValue)</c> for a key it did not resolve, and with a bare
/// <see cref="SpaceRole"/> that default is <c>0</c> — which today is not a declared
/// role, so it would fail closed by accident rather than by design. That accident
/// lasts exactly until someone renumbers the enum from zero, at which point every
/// caller holding no grant silently acquires whichever role landed on 0. Nullable
/// makes "no role" a value the type system states, so the safety does not depend on
/// the numbering.</para>
/// </summary>
public sealed class SpaceRoleBySpaceIdDataLoader(
    DbContextOptions<RocketWikiDbContext> dbOptions,
    ICurrentPrincipalAccessor principalAccessor,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : BatchDataLoader<Guid, SpaceRole?>(batchScheduler, options ?? new DataLoaderOptions())
{
    protected override async Task<IReadOnlyDictionary<Guid, SpaceRole?>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            // Anonymous holds no role anywhere. An empty map means every key resolves
            // to "no role" — fail closed, no query needed.
            return new Dictionary<Guid, SpaceRole?>();
        }

        // Own context per batch — see DataLoaderDbContext.
        await using var db = DataLoaderDbContext.Create(dbOptions);

        var grants = await db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.SpaceGrant && keys.Contains(r.SpaceId!.Value))
            .ToListAsync(cancellationToken);

        var roles = new Dictionary<Guid, SpaceRole?>();
        foreach (var group in grants.GroupBy(r => r.SpaceId!.Value))
        {
            // Absent rather than null-valued when the caller holds nothing here: the
            // dictionary's own "missing key" IS the no-role answer.
            if (EffectivePermissionCalculator.ComputeSpaceRole(group, principal) is { } role)
            {
                roles[group.Key] = role;
            }
        }

        return roles;
    }
}
