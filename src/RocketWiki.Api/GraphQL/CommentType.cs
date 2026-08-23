using HotChocolate.Types;
using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Explicit for the same reason as <see cref="PageRevisionType"/>: <c>Comment.Author</c>
/// navigates to <c>User</c>, which implicit inference would expose in full, including
/// `AttributesJson` (nationality - admin-only, design.md §6.2). The navigation is
/// rebound to a display-safe <see cref="UserRef"/> (id + display name, batched)
/// alongside the plain `AuthorUserId` Guid. `Replies` is ignored for now - nested
/// reply resolution isn't wired yet; flat comments (reconstructable client-side via
/// `parentCommentId`) are what's built today.
/// </summary>
public sealed class CommentType : ObjectType<Comment>
{
    protected override void Configure(IObjectTypeDescriptor<Comment> descriptor)
    {
        descriptor.Ignore(c => c.Page);
        descriptor.Ignore(c => c.ParentComment);
        descriptor.Ignore(c => c.Replies);

        // Rebound, not ignored-then-redefined: `Author` camelCases to exactly the
        // `author` field name being defined, and Ignore() wins permanently over a
        // later .Field() of the same name (see PageType's comment on this trap).
        // The rebind replaces the raw User navigation with the display-safe slice:
        // id + display name, batched, never the User entity (see
        // UserRefFieldResolvers' doc).
        descriptor.Field(c => c.Author)
            .Type<NonNullType<ObjectType<UserRef>>>()
            .ResolveWith<UserRefFieldResolvers>(r => r.GetCommentAuthorAsync(default!, default!, default));
    }
}
