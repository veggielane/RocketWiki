using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Services;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Batches per-page property resolution: ONE PageProperties query (joined to
/// PagePropertyKeys) for every page id in the batch, grouped in memory — how
/// <c>Page.properties</c> resolves for a single page and for a list of them alike,
/// without an N+1 (design.md §8's DataLoader rule).
///
/// No authorization decision here, and the reasoning is exactly the labels one
/// (design.md §6.4.2/§20): properties carry no restriction of their own, only the
/// parent page's, and every page id reaching this loader belongs to a <c>Page</c> that
/// already passed canView to be resolvable at all. The permission-filtered listing
/// question — "which pages carry property X" — has no query surface yet, and when it
/// gets one it must go through a canView-filtered read path, never through this loader.
///
/// Yields <see cref="PagePropertyValue"/>, never the <c>PageProperty</c> entity: that
/// carries a <c>Page</c> navigation, and returning it from a read path would open a
/// Page-shaped route around the object-level authorization every such field goes
/// through (the same reason <c>LabelRef</c> exists).
/// </summary>
public sealed class PagePropertyValuesByPageIdDataLoader(
    DbContextOptions<RocketWikiDbContext> dbOptions,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : GroupedDataLoader<Guid, PagePropertyValue>(batchScheduler, options ?? new DataLoaderOptions())
{
    protected override async Task<ILookup<Guid, PagePropertyValue>> LoadGroupedBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        // Own context per batch — see DataLoaderDbContext.
        await using var db = DataLoaderDbContext.Create(dbOptions);

        var rows = await db.PageProperties
            .Where(p => keys.Contains(p.PageId))
            .Select(p => new
            {
                p.PageId,
                p.PagePropertyKeyId,
                KeyName = p.PropertyKey!.Key,
                p.PropertyKey.SortOrder,
                p.Value,
            })
            .ToListAsync(cancellationToken);

        // Ordered in memory, ordinally on the key name, so the order is identical on
        // every provider - a database ORDER BY on a text column would sort by whatever
        // collation that provider defaults to (the same trap KeyNormalized exists for).
        return rows
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.KeyName, StringComparer.Ordinal)
            .ToLookup(
                r => r.PageId,
                r => new PagePropertyValue(r.PagePropertyKeyId, r.KeyName, r.Value, r.SortOrder));
    }
}
