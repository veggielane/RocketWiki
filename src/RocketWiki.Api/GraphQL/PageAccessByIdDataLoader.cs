using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Batches the DISCLOSED page read (<see cref="IPageReadService.GetPageAccessBatchAsync"/>)
/// across one GraphQL request — what <c>Page.linkTargets</c>, <c>Page.parent</c> and
/// <c>Page.parentDenial</c> resolve through (design.md §6.7 / §21.8), so a page with
/// forty links costs a constant number of queries and the parent's two fields share one
/// decision.
///
/// <para>Unlike <see cref="PageByIdDataLoader"/>, a key the caller may not view is not
/// omitted: it resolves to <c>PageAccess.Denied</c> carrying the denial the consumer
/// renders as a placeholder, and a missing page to <c>PageAccess.NotFound</c>, so the two
/// stay distinct until the field decides what each becomes on the wire. No denial is
/// audited here — a loader's per-request cache would under-count it anyway; the
/// consuming resolver audits where the design says a denial is audited (the parent
/// fields do; link targets are a pruned listing and do not).</para>
///
/// <para>Own context per batch, like every other loader — see
/// <see cref="DataLoaderDbContext"/>.</para>
/// </summary>
public sealed class PageAccessByIdDataLoader : BatchDataLoader<Guid, PageAccess>
{
    private readonly DbContextOptions<RocketWikiDbContext> _dbOptions;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public PageAccessByIdDataLoader(
        DbContextOptions<RocketWikiDbContext> dbOptions,
        ICurrentPrincipalAccessor principalAccessor,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _dbOptions = dbOptions;
        _principalAccessor = principalAccessor;
    }

    protected override async Task<IReadOnlyDictionary<Guid, PageAccess>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var principal = _principalAccessor.Current;
        if (principal is null)
        {
            // No token, no decision: every key reads as missing, which the fields render
            // as the same absence anonymous gets everywhere else.
            return new Dictionary<Guid, PageAccess>();
        }

        await using var db = DataLoaderDbContext.Create(_dbOptions);
        return await new PageReadService(db).GetPageAccessBatchAsync(keys, principal, cancellationToken);
    }
}
