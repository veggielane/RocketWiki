using HotChocolate.Types;
using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Binds the three feed rows to their <c>page</c> field, and hides the raw ids the rows
/// carry internally.
///
/// <para><b>Why <c>Ignore(PageId)</c> on every one of them</b>, the same as
/// <c>SearchHitType</c>: a page id on the row is how the loader finds the page, not
/// something a client should read off the row instead of selecting <c>page { id }</c>.
/// Leaving it exposed invites a client to use the row's copy — and a copy is exactly what
/// routes around the object-level Page resolver these types exist to go through
/// (§6.7/§8). The author id is hidden for the same reason: <c>author</c> resolves a
/// <c>UserRef</c>, which is the display-safe projection, rather than handing out a bare
/// user id nothing has vetted.</para>
/// </summary>
public sealed class ActivityFeedItemType : ObjectType<ActivityFeedItem>
{
    protected override void Configure(IObjectTypeDescriptor<ActivityFeedItem> descriptor)
    {
        descriptor.Ignore(i => i.PageId);
        descriptor.Ignore(i => i.AuthorUserId);

        descriptor.Field("page")
            .Type<NonNullType<PageType>>()
            .ResolveWith<HomeFeedFieldResolvers>(r => r.GetActivityPageAsync(default!, default!, default));

        descriptor.Field("author")
            .Type<NonNullType<ObjectType<UserRef>>>()
            .ResolveWith<HomeFeedFieldResolvers>(r => r.GetAuthorAsync(default!, default!, default));
    }
}

public sealed class StaleContentItemType : ObjectType<StaleContentItem>
{
    protected override void Configure(IObjectTypeDescriptor<StaleContentItem> descriptor)
    {
        descriptor.Ignore(i => i.PageId);

        descriptor.Field("page")
            .Type<NonNullType<PageType>>()
            .ResolveWith<HomeFeedFieldResolvers>(r => r.GetStalePageAsync(default!, default!, default));
    }
}

public sealed class RecentlyViewedItemType : ObjectType<RecentlyViewedItem>
{
    protected override void Configure(IObjectTypeDescriptor<RecentlyViewedItem> descriptor)
    {
        descriptor.Ignore(i => i.PageId);

        descriptor.Field("page")
            .Type<NonNullType<PageType>>()
            .ResolveWith<HomeFeedFieldResolvers>(r => r.GetViewedPageAsync(default!, default!, default));
    }
}

/// <summary>
/// Every feed row resolves its page through <see cref="PageByIdDataLoader"/> — i.e.
/// through IPageReadService's canView gate — like every other Page-returning field.
///
/// <para>The feed already filtered these ids, so the loader normally just dedupes and
/// batches. The value of going through it anyway is the case where it does not: a page
/// restricted between the feed's filter and this resolution returns null, which violates
/// the non-null contract and errors the field rather than rendering a row for a page the
/// caller may no longer view. Failing loudly there is right — the alternative is a
/// homepage quietly showing a title someone just lost access to.</para>
/// </summary>
public sealed class HomeFeedFieldResolvers
{
    public async Task<Page?> GetActivityPageAsync(
        [Parent] ActivityFeedItem item, PageByIdDataLoader pageByIdLoader, CancellationToken cancellationToken) =>
        await pageByIdLoader.LoadAsync(item.PageId, cancellationToken);

    public async Task<Page?> GetStalePageAsync(
        [Parent] StaleContentItem item, PageByIdDataLoader pageByIdLoader, CancellationToken cancellationToken) =>
        await pageByIdLoader.LoadAsync(item.PageId, cancellationToken);

    public async Task<Page?> GetViewedPageAsync(
        [Parent] RecentlyViewedItem item, PageByIdDataLoader pageByIdLoader, CancellationToken cancellationToken) =>
        await pageByIdLoader.LoadAsync(item.PageId, cancellationToken);

    /// <summary>
    /// Batched through <see cref="UserRefByIdDataLoader"/>, so a feed page costs one Users
    /// query rather than one per row — and yields the display-safe projection rather than
    /// the User entity, whose AttributesJson mirrors nationality (§6.2).
    /// </summary>
    public async Task<UserRef?> GetAuthorAsync(
        [Parent] ActivityFeedItem item, UserRefByIdDataLoader userRefLoader, CancellationToken cancellationToken) =>
        await userRefLoader.LoadAsync(item.AuthorUserId, cancellationToken);
}
