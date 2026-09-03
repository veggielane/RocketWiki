using HotChocolate;
using HotChocolate.Types;
using RocketWiki.Api.Markings;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Explicit type for the same two reasons as <see cref="SearchConnectionType"/>:
/// <c>MatchedPageIds</c> is transport rather than contract — it exists so the aggregate can
/// be folded without a second query — and the aggregate is a resolver, so a client that
/// never asks for it never pays for the marking query.
/// </summary>
public sealed class PageQueryConnectionType : ObjectType<PageQueryConnection>
{
    protected override void Configure(IObjectTypeDescriptor<PageQueryConnection> descriptor)
    {
        descriptor.Ignore(c => c.MatchedPageIds);

        // Nullable: a query that matched nothing has nothing to mark, and the honest answer
        // to "what classification is this empty list" is no label at all (design.md §21.13).
        descriptor.Field("aggregateMarking")
            .Type<ObjectType<AggregateMarkingLabel>>()
            .ResolveWith<PageQueryConnectionFieldResolvers>(r => r.GetAggregateMarkingAsync(default!, default!, default!, default));
    }
}

public sealed class PageQueryConnectionFieldResolvers
{
    /// <summary>
    /// design.md §21.13: a results list is a compilation, and a compilation carries the
    /// classification of its most sensitive constituent.
    ///
    /// <para>Scope is the whole permission-filtered result set the connection reports — the
    /// set <c>totalCount</c> counts — not the edges of the page being fetched, so every page
    /// of the connection reports the same label. A per-page aggregate would make the client
    /// <i>combine</i> aggregates as it loads more, which is the caveat-conjunction rule
    /// reimplemented in TypeScript (§21.1 forbids exactly that), and a label that drops as
    /// you scroll would say the results got less sensitive when all that happened is you
    /// paged past the sensitive ones.</para>
    ///
    /// <para>Nothing the caller was not shown contributes: the id set arrives already
    /// canView-filtered from <c>IPageQueryService</c>, so a page above their clearance or
    /// behind a restriction they fail is absent and cannot raise this label. Resolution rides
    /// <see cref="PageMarkingByPageIdDataLoader"/>, so asking for this and for
    /// <c>edges { node { page { marking } } }</c> costs one PageMarkings query in total and
    /// the aggregate can never disagree with the badges beneath it.</para>
    /// </summary>
    public async Task<AggregateMarkingLabel?> GetAggregateMarkingAsync(
        [Parent] PageQueryConnection connection,
        PageMarkingByPageIdDataLoader markingLoader,
        [Service] SelectorCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (connection.MatchedPageIds.Count == 0)
        {
            return null;
        }

        var markings = await markingLoader.LoadAsync(connection.MatchedPageIds, cancellationToken);
        return AggregateMarkingLabel.Of(markings.Select(m => m ?? ProtectiveMarking.FailClosed), catalog);
    }
}

/// <summary>
/// A matched row exposes exactly one field, <c>page</c>, resolved through the
/// object-level-authorized loader. <c>PageId</c> is transport and is ignored: exposing it —
/// or a title, or a space key — would be an unauthorized copy of page data that bypasses the
/// path every <c>Page</c> field is served through (design.md §6.7/§8, and see
/// <see cref="SearchHitType"/>, which ignores Title/SpaceKey for the same reason).
/// </summary>
public sealed class PageQueryRowType : ObjectType<PageQueryRow>
{
    protected override void Configure(IObjectTypeDescriptor<PageQueryRow> descriptor)
    {
        descriptor.Ignore(r => r.PageId);

        descriptor.Field("page")
            .Type<NonNullType<PageType>>()
            .ResolveWith<PageQueryRowFieldResolvers>(r => r.GetPageAsync(default!, default!, default));
    }
}

public sealed class PageQueryRowFieldResolvers
{
    /// <summary>
    /// Routes through <see cref="PageByIdDataLoader"/> — i.e. through IPageReadService's
    /// canView gate — like every other Page-returning field. The service already filtered
    /// this id, so the loader normally just dedupes and batches; if the page vanished or its
    /// restrictions tightened between execution and resolution, the null here violates the
    /// field's non-null contract and the whole query errors out, which is failing closed
    /// rather than fabricating a row for a page the caller can no longer view.
    /// </summary>
    public async Task<Page?> GetPageAsync(
        [Parent] PageQueryRow row, PageByIdDataLoader pageByIdLoader, CancellationToken cancellationToken) =>
        await pageByIdLoader.LoadAsync(row.PageId, cancellationToken);
}
