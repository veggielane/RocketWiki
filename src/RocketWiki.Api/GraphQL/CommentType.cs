using HotChocolate.Types;
using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Explicit for the same reason as <see cref="PageRevisionType"/>: <c>Comment.Author</c>
/// navigates to <c>User</c>, which implicit inference would expose in full, including
/// `AttributesJson` (nationality - admin-only, design.md §6.2). Only the plain
/// `AuthorUserId` Guid is exposed. `Replies` is also ignored for now - nested reply
/// resolution isn't wired yet; flat comments (reconstructable client-side via
/// `parentCommentId`) are what's built today.
/// </summary>
public sealed class CommentType : ObjectType<Comment>
{
    protected override void Configure(IObjectTypeDescriptor<Comment> descriptor)
    {
        descriptor.Ignore(c => c.Page);
        descriptor.Ignore(c => c.Author);
        descriptor.Ignore(c => c.ParentComment);
        descriptor.Ignore(c => c.Replies);
    }
}
