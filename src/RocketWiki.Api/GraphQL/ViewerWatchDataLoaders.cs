using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Identity;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Batches "is the caller watching this page" for <c>Page.viewerIsWatching</c> — one
/// Watches query per batch keyed on the caller's own user id, so a list of pages
/// never becomes one query per page (design.md §8's DataLoader rule). Viewer-relative
/// by construction: the acting user comes from the request identity
/// (<see cref="IActingUserAccessor"/>), never from an argument, so a caller can only
/// ever read their own watch rows. No acting user (anonymous) resolves everything to
/// false. No authorization decision here: the parent Page already passed canView to
/// exist as a GraphQL object, and the flag reveals only the caller's own
/// subscription-bookkeeping row, not content.
/// </summary>
public sealed class ViewerWatchesPageDataLoader(
    DbContextOptions<RocketWikiDbContext> dbOptions,
    IActingUserAccessor actingUserAccessor,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : BatchDataLoader<Guid, bool>(batchScheduler, options ?? new DataLoaderOptions())
{
    private readonly IActingUserAccessor _actingUserAccessor = actingUserAccessor;

    protected override async Task<IReadOnlyDictionary<Guid, bool>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var actingUserId = _actingUserAccessor.ActingUserId;
        if (actingUserId is null)
        {
            return keys.ToDictionary(k => k, _ => false);
        }

        // Own context per batch — see DataLoaderDbContext.
        await using var db = DataLoaderDbContext.Create(dbOptions);

        var watched = await db.Watches
            .Where(w => w.UserId == actingUserId.Value && w.PageId != null && keys.Contains(w.PageId.Value))
            .Select(w => w.PageId!.Value)
            .ToListAsync(cancellationToken);
        var watchedSet = watched.ToHashSet();

        // Every key maps explicitly - a missing dictionary entry must never be the
        // representation of "not watching".
        return keys.ToDictionary(k => k, watchedSet.Contains);
    }
}

/// <summary>Space twin of <see cref="ViewerWatchesPageDataLoader"/> for
/// <c>Space.viewerIsWatching</c> — same shape, same reasoning.</summary>
public sealed class ViewerWatchesSpaceDataLoader(
    DbContextOptions<RocketWikiDbContext> dbOptions,
    IActingUserAccessor actingUserAccessor,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : BatchDataLoader<Guid, bool>(batchScheduler, options ?? new DataLoaderOptions())
{
    private readonly IActingUserAccessor _actingUserAccessor = actingUserAccessor;

    protected override async Task<IReadOnlyDictionary<Guid, bool>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var actingUserId = _actingUserAccessor.ActingUserId;
        if (actingUserId is null)
        {
            return keys.ToDictionary(k => k, _ => false);
        }

        // Own context per batch — see DataLoaderDbContext.
        await using var db = DataLoaderDbContext.Create(dbOptions);

        var watched = await db.Watches
            .Where(w => w.UserId == actingUserId.Value && w.SpaceId != null && keys.Contains(w.SpaceId.Value))
            .Select(w => w.SpaceId!.Value)
            .ToListAsync(cancellationToken);
        var watchedSet = watched.ToHashSet();

        return keys.ToDictionary(k => k, watchedSet.Contains);
    }
}
