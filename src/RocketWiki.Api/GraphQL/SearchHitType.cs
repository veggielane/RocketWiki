using HotChocolate.Types;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Explicit type for the same reason as <see cref="PageType"/>: the schema exposes
/// exactly the frontend's contract (snippet / headingPath / anchorId / page) and
/// nothing the record happens to carry. PageId/Title/SpaceKey are transport between
/// the service and this type — exposing them as fields would duplicate `page { id
/// title spaceKey }` with unauthorized copies: the record's own Title/SpaceKey would
/// bypass the object-level path every Page field is served through. They came from a
/// canView-passing hit, so they're not a leak today — but object-level authorization
/// (design.md §6.7/§8) means Page data flows through Page resolvers, not around them.
/// </summary>
public sealed class SearchHitType : ObjectType<SearchHit>
{
    protected override void Configure(IObjectTypeDescriptor<SearchHit> descriptor)
    {
        descriptor.Ignore(h => h.PageId);
        descriptor.Ignore(h => h.Title);
        descriptor.Ignore(h => h.SpaceKey);

        descriptor.Field("page")
            .Type<NonNullType<PageType>>()
            .ResolveWith<SearchHitFieldResolvers>(r => r.GetPageAsync(default!, default!, default));
    }
}

public sealed class SearchHitFieldResolvers
{
    /// <summary>
    /// Routes through <see cref="PageByIdDataLoader"/> — i.e. through
    /// IPageReadService's canView gate — like every other Page-returning field
    /// (design.md §6.7: object-level, not root-level). The service already filtered
    /// this hit, so the loader normally just dedupes/batches; if the page vanished or
    /// its restrictions tightened between the search and this resolution, the null
    /// here violates the field's non-null contract and the whole search field errors
    /// out — failing closed rather than fabricating a hit for a page the caller can
    /// no longer view.
    /// </summary>
    public async Task<Page?> GetPageAsync(
        [Parent] SearchHit hit, PageByIdDataLoader pageByIdLoader, CancellationToken cancellationToken) =>
        await pageByIdLoader.LoadAsync(hit.PageId, cancellationToken);
}
