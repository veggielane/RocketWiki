using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Batches <see cref="IPageGraphService.GetPageLinksAsync"/> across one GraphQL request —
/// what <c>Page.outboundLinks</c>, <c>inboundLinks</c>, <c>outboundLinkCount</c> and
/// <c>inboundLinkCount</c> all resolve through, so selecting the four fields on one page
/// costs one decision and one set of queries, and the two counts are read off the very
/// lists the two list fields return (see <see cref="PageFieldResolvers"/>).
///
/// <para>The value is the service's <see cref="ReadResult{T}"/> unchanged, not its
/// collapsed neighbours: a key the caller may not view resolves to <c>Denied</c> carrying
/// the reason, and a page that is missing or trashed to <c>NotFound</c>, so the consuming
/// resolver can audit the one and stay silent on the other (design.md §7 /
/// <c>ReadDenialAudit</c>'s doc) before both become the same empty list. No audit is
/// written here — a loader's per-request cache would under-count it.</para>
///
/// <para><b>Honest about its batching.</b> The Core method is single-page-shaped, so a
/// batch of N distinct pages is N service calls inside one dispatch (each a constant five
/// queries), not one round of queries for the whole batch as
/// <see cref="VisibleChildIdsByPageIdDataLoader"/> gets from its batch-shaped Core method.
/// What this loader guarantees is the per-page dedup — four fields, one call — and a
/// single dispatch point; a batch-shaped <c>GetPageLinksAsync</c> would be the Core change
/// that turns N into a constant, and belongs there rather than in a loader that assembled
/// authorization inputs of its own.</para>
///
/// <para>Own context per batch, like every other loader — see
/// <see cref="DataLoaderDbContext"/>.</para>
/// </summary>
public sealed class PageLinkNeighboursByPageIdDataLoader : BatchDataLoader<Guid, ReadResult<PageLinkNeighbours>>
{
    private readonly DbContextOptions<RocketWikiDbContext> _dbOptions;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public PageLinkNeighboursByPageIdDataLoader(
        DbContextOptions<RocketWikiDbContext> dbOptions,
        ICurrentPrincipalAccessor principalAccessor,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _dbOptions = dbOptions;
        _principalAccessor = principalAccessor;
    }

    protected override async Task<IReadOnlyDictionary<Guid, ReadResult<PageLinkNeighbours>>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var principal = _principalAccessor.Current;
        if (principal is null)
        {
            // No token, no decision: every key reads as absent, which the fields render
            // as the same empty list anonymous gets everywhere else.
            return new Dictionary<Guid, ReadResult<PageLinkNeighbours>>();
        }

        await using var db = DataLoaderDbContext.Create(_dbOptions);
        var service = new PageGraphService(db);
        var results = new Dictionary<Guid, ReadResult<PageLinkNeighbours>>(keys.Count);
        foreach (var key in keys)
        {
            results[key] = await service.GetPageLinksAsync(key, principal, cancellationToken);
        }

        return results;
    }
}
