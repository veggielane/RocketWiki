using GreenDonut;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Batches and dedupes <c>IPageReadService.GetPageAsync</c> calls within one GraphQL
/// request (design.md §8's DataLoader requirement) - used by
/// <see cref="PageFieldResolvers.GetChildrenAsync"/>, previously an unbatched N+1.
///
/// Honest about what this does and doesn't fix: IPageReadService has no bulk-fetch
/// method, so the underlying calls still run one per id (in parallel, not
/// sequentially) rather than collapsing into a single query. What this DataLoader
/// does guarantee is per-request deduplication - the same page id requested via
/// multiple paths in one query only ever calls the service once - and parallelism
/// across the batch. A true single-query batch would need a new
/// IPageReadService method (e.g. GetPagesAsync(IEnumerable&lt;Guid&gt;, Principal)) -
/// a Core change, not made here.
/// </summary>
public sealed class PageByIdDataLoader : BatchDataLoader<Guid, Page>
{
    private readonly IPageReadService _readService;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public PageByIdDataLoader(
        IPageReadService readService,
        ICurrentPrincipalAccessor principalAccessor,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _readService = readService;
        _principalAccessor = principalAccessor;
    }

    protected override async Task<IReadOnlyDictionary<Guid, Page>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var principal = _principalAccessor.Current;
        if (principal is null)
        {
            return new Dictionary<Guid, Page>();
        }

        var loaded = await Task.WhenAll(keys.Select(async id =>
            (Id: id, Page: await _readService.GetPageAsync(id, principal, cancellationToken))));

        // Ids that failed canView (or vanished) are simply omitted - BatchDataLoader
        // treats a missing key as "no value", which is exactly the absent-not-forbidden
        // behavior every other Page path already has.
        return loaded
            .Where(r => r.Page is not null)
            .ToDictionary(r => r.Id, r => r.Page!);
    }
}
