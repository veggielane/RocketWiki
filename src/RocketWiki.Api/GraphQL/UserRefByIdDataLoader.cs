using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The id + display-name projection of a local <c>User</c> row, and the ONLY shape of
/// that row this schema ever hands to a client outside the admin-only audit viewer.
/// Deliberately not the entity: <c>User.AttributesJson</c> mirrors registered
/// attributes (nationality — sensitive personal data, instance-admin-visible only,
/// design.md §6.2), and exposing the entity would leak it to anyone who can read a
/// comment. Display only, per design.md §6.1: the mirror "is for display/admin UI
/// only" — nothing that resolves a <c>UserRef</c> feeds an authorization decision,
/// which always evaluates the token, never this table.
/// </summary>
public sealed record UserRef(Guid Id, string DisplayName);

/// <summary>
/// Batches display-name resolution (design.md §8's DataLoader rule): one Users query
/// per request batch, not one per comment/attachment/audit row in a list. Used by
/// <c>Comment.author</c>, <c>Attachment.uploadedBy</c>, <c>Page.deletedBy</c>, and
/// <c>AuditEvent.userDisplayName</c> (previously an unbatched per-row query).
/// </summary>
public sealed class UserRefByIdDataLoader(
    RocketWikiDbContext db,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : BatchDataLoader<Guid, UserRef>(batchScheduler, options ?? new DataLoaderOptions())
{
    private readonly RocketWikiDbContext _db = db;

    protected override async Task<IReadOnlyDictionary<Guid, UserRef>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken) =>
        await _db.Users.AsNoTracking()
            .Where(u => keys.Contains(u.Id))
            .Select(u => new UserRef(u.Id, u.DisplayName))
            .ToDictionaryAsync(u => u.Id, u => u, cancellationToken);
}
