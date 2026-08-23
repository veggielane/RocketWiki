using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Batches <c>PageRevision.contributors</c> (design.md §8 co-editing attribution):
/// one PageRevisionContributors-joined-to-Users query per request batch, so a whole
/// revision-history listing costs a constant number of queries (design.md §8's
/// DataLoader rule). Projects to <see cref="UserRef"/> — the only User shape this
/// schema hands out (never the entity; see UserRefByIdDataLoader for the
/// AttributesJson leak that guards against). No authorization decision here: a
/// contributor row is attribution on a revision the caller already resolved through
/// the canView-gated revisions path, the same byline reasoning as Comment.author.
/// </summary>
public sealed class ContributorsByRevisionIdDataLoader(
    RocketWikiDbContext db,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : GroupedDataLoader<Guid, UserRef>(batchScheduler, options ?? new DataLoaderOptions())
{
    private readonly RocketWikiDbContext _db = db;

    protected override async Task<ILookup<Guid, UserRef>> LoadGroupedBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var rows = await _db.PageRevisionContributors.AsNoTracking()
            .Where(c => keys.Contains(c.PageRevisionId))
            .Select(c => new
            {
                c.PageRevisionId,
                c.UserId,
                c.User!.DisplayName,
                HasAvatar = _db.UserAvatars.Any(a => a.UserId == c.UserId),
            })
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(r => r.DisplayName, StringComparer.Ordinal)
            .ToLookup(r => r.PageRevisionId, r => new UserRef(r.UserId, r.DisplayName, r.HasAvatar));
    }
}
