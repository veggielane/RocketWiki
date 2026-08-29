using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The id + name projection of a <c>Label</c> row. Exists because the label mutations
/// (<c>attachLabel</c>/<c>detachLabel</c>) take label ids while the original read
/// paths (<c>Query.labels</c>, <c>Page.labels</c>) returned names only, making an
/// edit round-trip impossible from the SPA (design.md §8's known-deltas list). Not
/// the <c>Label</c> entity: that carries <c>Space</c>/<c>PageLabels</c> navigations,
/// and returning it from a read path would open Page/Space-shaped routes around the
/// object-level authorization every such field must go through.
/// </summary>
public sealed record LabelRef(Guid Id, Guid SpaceId, string Name);

/// <summary>
/// Batches per-page label resolution: ONE PageLabels query (joined to Label) for
/// every page id in the batch, grouped in memory — how <c>Page.labels</c>,
/// <c>Page.labelDetails</c>, and <c>PageTreeNode.labels</c> all resolve without an
/// N+1 (design.md §8's DataLoader rule). For a whole-tree query the executor
/// suspends every node's labels resolver on this loader before dispatching, so the
/// space's visible page-label pairs arrive in a single query regardless of tree
/// depth. No authorization decision here: labels carry no restriction of their own,
/// only the parent page's (design.md §6.4.2), and every page id reaching this loader
/// belongs to a Page/PageTreeNode that already passed canView to be resolvable at
/// all — the permission-filtered listing question ("which pages carry label X") goes
/// through the canView-filtered read paths, never through this loader.
/// </summary>
public sealed class LabelRefsByPageIdDataLoader(
    DbContextOptions<RocketWikiDbContext> dbOptions,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : GroupedDataLoader<Guid, LabelRef>(batchScheduler, options ?? new DataLoaderOptions())
{
    protected override async Task<ILookup<Guid, LabelRef>> LoadGroupedBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        // Own context per batch — see DataLoaderDbContext.
        await using var db = DataLoaderDbContext.Create(dbOptions);

        var pairs = await db.PageLabels
            .Where(pl => keys.Contains(pl.PageId))
            .Select(pl => new { pl.PageId, pl.LabelId, pl.Label!.SpaceId, pl.Label.Name })
            .ToListAsync(cancellationToken);

        return pairs
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToLookup(p => p.PageId, p => new LabelRef(p.LabelId, p.SpaceId, p.Name));
    }
}
