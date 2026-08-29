using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Batches `Page.spaceKey` resolution (one Spaces query per request, not one per
/// page in a search result list — design.md §8's DataLoader rule). Key metadata
/// only, no authorization decision here: the parent Page already passed canView to
/// exist as a GraphQL object at all, and its space's key is structural addressing
/// (it's in every page URL), not restricted content.
///
/// IgnoreQueryFilters: the Spaces soft-delete filter hides *archived* spaces from
/// browsing (Query.Spaces), but a Page that was legitimately resolved — e.g. out of
/// the trash of an archived space — must still be addressable; resolving its key is
/// not browsing the space.
/// </summary>
public sealed class SpaceKeyBySpaceIdDataLoader(
    DbContextOptions<RocketWikiDbContext> dbOptions,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : BatchDataLoader<Guid, string>(batchScheduler, options ?? new DataLoaderOptions())
{
    protected override async Task<IReadOnlyDictionary<Guid, string>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        // Own context per batch — see DataLoaderDbContext.
        await using var db = DataLoaderDbContext.Create(dbOptions);

        return await db.Spaces
            .IgnoreQueryFilters()
            .Where(s => keys.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Key, cancellationToken);
    }
}
