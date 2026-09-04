using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Identity;
using RocketWiki.Data;
using RocketWiki.Data.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Batches <c>Page.children</c> across a whole GraphQL request (design.md §8: "DataLoaders
/// batch children, labels, and authors — no N+1 queries when resolving the tree or search
/// results"). <c>children</c> was the one list-resolved field left without one.
///
/// <para>What it replaced is worth stating, because the cost was not obvious from the
/// resolver: <see cref="PageFieldResolvers.GetChildrenAsync"/> used to derive child ids by
/// walking the WHOLE space tree and locating the parent inside it. That walk materializes
/// every live page in the space plus its restrictions and markings, and nothing memoized
/// it — so a twenty-hit search selecting <c>page { children { id } }</c> ran twenty
/// full-space walks. Here the whole batch costs a constant four queries, and it fixes a
/// correctness bug at the same time (see
/// <see cref="IPageReadService.GetVisibleChildIdsAsync"/>: children are gated
/// individually, so a page whose ancestor is beyond the caller's grants no longer
/// answers <c>children</c> with a misleading empty list).</para>
///
/// <para>The Core method it calls is batch-shaped, so the keys collapse into one round of
/// queries rather than one call per key. Permission evaluation stays entirely inside
/// RocketWiki.Data — <c>PermissionContextLoader</c> is internal there, and a loader that
/// assembled authorization inputs itself would be exactly the second enforcement path
/// §6.7 warns about.</para>
///
/// <para><b>Own context</b>, like every other loader (see <see cref="DataLoaderDbContext"/>).
/// It originally injected the request-scoped <c>IPageReadService</c>, which is safe in
/// isolation — one call, no internal fan-out — but not in company: loaders dispatch in the
/// same tick, so sharing the request context with <see cref="PageByIdDataLoader"/> put two
/// batches on one <c>DbContext</c> for a query as ordinary as
/// <c>search { edges { node { page { children { id } } } } }</c>. "Safe unless something
/// else runs at the same time" is not a property a DataLoader can have.</para>
/// </summary>
public sealed class VisibleChildIdsByPageIdDataLoader : BatchDataLoader<Guid, IReadOnlyList<Guid>>
{
    private readonly DbContextOptions<RocketWikiDbContext> _dbOptions;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public VisibleChildIdsByPageIdDataLoader(
        DbContextOptions<RocketWikiDbContext> dbOptions,
        ICurrentPrincipalAccessor principalAccessor,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _dbOptions = dbOptions;
        _principalAccessor = principalAccessor;
    }

    protected override async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var principal = _principalAccessor.Current;
        if (principal is null)
        {
            // Anonymous sees nothing anywhere else either; a missing key reads as the
            // empty list the resolver already returns for "no visible children".
            return new Dictionary<Guid, IReadOnlyList<Guid>>();
        }

        await using var db = DataLoaderDbContext.Create(_dbOptions);
        return await new PageReadService(db).GetVisibleChildIdsAsync(keys, principal, cancellationToken);
    }
}
