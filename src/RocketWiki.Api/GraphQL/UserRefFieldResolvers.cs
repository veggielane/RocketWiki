using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Display-name resolution for the author-shaped ids the SPA renders
/// (<c>Comment.author</c>, <c>Attachment.uploadedBy</c>, trashed-page
/// <c>Page.deletedBy</c>) — all through <see cref="UserRefByIdDataLoader"/> so a
/// comment thread or attachment list is one batched Users query, never one per row
/// (design.md §8's DataLoader rule).
///
/// design.md §6.1 boundary, stated once for all three: these fields READ the local
/// User mirror for display, which is exactly what the mirror exists for — they feed
/// no authorization decision, and no rule engine path consumes a <c>UserRef</c>.
/// The parent object (Comment, Attachment, trashed Page) has already passed the
/// canView / trash-listing gates to exist as a GraphQL object at all, and a byline
/// adds no restricted content on top: it is the same attribution the entity's
/// <c>*UserId</c> column already exposes, resolved to a human-readable name.
/// </summary>
public sealed class UserRefFieldResolvers
{
    /// <summary>Non-null by FK: a Comment row cannot exist without its author User row
    /// (shadow users from sync included, design.md §12). A miss is corrupt state worth
    /// crashing on — same call as <c>PageFieldResolvers.GetSpaceKeyAsync</c>.</summary>
    public async Task<UserRef> GetCommentAuthorAsync(
        [Parent] Comment comment, UserRefByIdDataLoader userLoader, CancellationToken cancellationToken) =>
        await userLoader.LoadAsync(comment.AuthorUserId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Comment {comment.Id} references author user {comment.AuthorUserId}, which does not exist.");

    /// <summary>Non-null by FK, same reasoning as <see cref="GetCommentAuthorAsync"/>.</summary>
    public async Task<UserRef> GetUploadedByAsync(
        [Parent] Attachment attachment, UserRefByIdDataLoader userLoader, CancellationToken cancellationToken) =>
        await userLoader.LoadAsync(attachment.UploadedByUserId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Attachment {attachment.Id} references uploader user {attachment.UploadedByUserId}, which does not exist.");

    /// <summary>Null for a live page (no deleter exists); resolved for trashed pages so
    /// the trash listing can say who deleted what without a per-row query.</summary>
    public async Task<UserRef?> GetDeletedByAsync(
        [Parent] Page page, UserRefByIdDataLoader userLoader, CancellationToken cancellationToken) =>
        page.DeletedByUserId is null
            ? null
            : await userLoader.LoadAsync(page.DeletedByUserId.Value, cancellationToken);
}
