using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Entities;
using RocketWiki.Data;
using RocketWiki.Data.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Materializes a <c>Page</c> from an id, batched across one GraphQL request (design.md
/// §8's DataLoader requirement) — used by <see cref="PageFieldResolvers.GetChildrenAsync"/>,
/// by <c>SearchHitType</c>'s <c>page</c> field, and by RQL rows.
///
/// <para><b>Own context, and a true batch.</b> Both halves matter and both were wrong.
/// This loader used to inject the request-scoped <c>IPageReadService</c> and fan
/// <c>Task.WhenAll</c> across the keys, which is N concurrent operations on ONE
/// <c>DbContext</c> — the exact fault <see cref="DataLoaderDbContext"/> exists to prevent,
/// and the last loader still doing it. It was invisible to the SQLite tier (in-process,
/// async completes synchronously, so the dispatches serialize) and to the SQL Server tier
/// only because its one GraphQL fixture had a page with no children and ran no search.
/// Against a real server it throws "a second operation was started on this context
/// instance" and the SPA shows "Couldn't load this page" — pinned now by
/// <c>ParallelFieldResolutionTests.PageByIdDataLoader_WithSeveralKeysInOneBatch_…</c>.
///
/// <para>Serializing the loop over a private context would have fixed the crash and left
/// N×3 round trips per batch. <c>IPageReadService.GetPagesAsync</c> is instead a real
/// batch — four queries for the whole set — so this is no longer the "dedupes and
/// parallelizes but does not truly batch" loader its doc used to apologize for.</para>
///
/// <para>Keys that failed <c>canView</c> (or vanished) are simply omitted:
/// <c>BatchDataLoader</c> treats a missing key as "no value", which is the
/// absent-not-forbidden behaviour every other Page path has (§6.7). No denial auditing
/// here, deliberately — a loader's per-request result cache would under-count it anyway,
/// and denials are audited where a resolver consumes a <c>ReadResult</c>.</para>
/// </summary>
public sealed class PageByIdDataLoader : BatchDataLoader<Guid, Page>
{
    private readonly DbContextOptions<RocketWikiDbContext> _dbOptions;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public PageByIdDataLoader(
        DbContextOptions<RocketWikiDbContext> dbOptions,
        ICurrentPrincipalAccessor principalAccessor,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _dbOptions = dbOptions;
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

        // Own context per batch — see DataLoaderDbContext. Constructed here rather than
        // injected for the same reason as PagePermissionFactsDataLoader: injecting the
        // service would bring the request-scoped context back in with it, which is the
        // thing that raced. Mirrors Program's registration exactly.
        await using var db = DataLoaderDbContext.Create(_dbOptions);
        var readService = new PageReadService(db);

        return await readService.GetPagesAsync(keys, principal, cancellationToken);
    }
}
