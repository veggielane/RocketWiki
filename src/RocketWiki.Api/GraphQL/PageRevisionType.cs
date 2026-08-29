using HotChocolate.Types;
using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Explicit type (not implicit inference) for exactly one reason: `PageRevision.Author`
/// navigates to <c>User</c>, and implicit inference would expose every public property
/// on it — including `AttributesJson`, the mirrored registered attributes (nationality)
/// design.md §6.2 marks admin-visible only. Ignoring the navigation here is what keeps
/// that off the schema; only the plain `AuthorUserId` Guid is exposed.
///
/// <c>contributors</c> (design.md §8 co-editing): the PageRevisionContributor rows a
/// session save recorded, as <see cref="UserRef"/>s — empty for solo saves.
/// AuthorUserId stays "who pressed save"; this is who typed.
/// </summary>
public sealed class PageRevisionType : ObjectType<PageRevision>
{
    protected override void Configure(IObjectTypeDescriptor<PageRevision> descriptor)
    {
        descriptor.Ignore(r => r.Page);

        // Rebound rather than ignored: `Author` camelCases to the `author` field being
        // defined, and Ignore() wins permanently over a later .Field() of the same name
        // (the trap PageType and CommentType both document). The rebind replaces the raw
        // User navigation with the display-safe slice — id and display name, batched.
        //
        // This is who pressed SAVE. `contributors` below is who typed, which for a solo
        // save is nobody — so a history list that wanted one always-present name would
        // have had only a Guid before this.
        descriptor.Field(r => r.Author)
            .Type<NonNullType<ObjectType<UserRef>>>()
            .ResolveWith<UserRefFieldResolvers>(r => r.GetRevisionAuthorAsync(default!, default!, default));

        descriptor.Field("contributors")
            .Type<NonNullType<ListType<NonNullType<ObjectType<UserRef>>>>>()
            .ResolveWith<PageRevisionFieldResolvers>(r => r.GetContributorsAsync(default!, default!, default));
    }
}

/// <summary>Display-only resolution through the batched loader — same §6.1 stance as
/// UserRefFieldResolvers: the local mirror feeds bylines, never a decision.</summary>
public sealed class PageRevisionFieldResolvers
{
    public async Task<IReadOnlyList<UserRef>> GetContributorsAsync(
        [Parent] PageRevision revision,
        ContributorsByRevisionIdDataLoader loader,
        CancellationToken cancellationToken) =>
        await loader.LoadAsync(revision.Id, cancellationToken) ?? [];
}
