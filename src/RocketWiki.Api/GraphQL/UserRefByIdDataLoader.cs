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
///
/// <see cref="HasAvatar"/> is the phase-2 contract for rendering: image when true
/// (fetched from <c>GET /users/{id}/avatar</c>, authenticated, through the same
/// blob-URL pattern as inline images — design.md §10), initials when false — no
/// probing GETs for users who never uploaded one. A boolean rather than an avatarUrl
/// string on purpose: the URL is derivable from <c>id</c>, and a nullable URL field
/// would invite treating it as a directly-embeddable <c>img src</c>, which §10's
/// no-unauthenticated-URL rule forbids.
/// </summary>
public sealed record UserRef(Guid Id, string DisplayName, bool HasAvatar);

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
        // HasAvatar is a correlated EXISTS inside the same single batched query —
        // still one Users round trip per request batch, not one per row and not a
        // second query. Provider-agnostic LINQ (translates on SQLite and SQL Server).
        await _db.Users.AsNoTracking()
            .Where(u => keys.Contains(u.Id))
            .Select(u => new UserRef(u.Id, u.DisplayName, _db.UserAvatars.Any(a => a.UserId == u.Id)))
            .ToDictionaryAsync(u => u.Id, u => u, cancellationToken);
}
