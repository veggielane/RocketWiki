using HotChocolate;
using HotChocolate.Types;
using RocketWiki.Api.Markings;
using RocketWiki.Core.Access;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Explicit type for two reasons: <c>HitPageIds</c> is transport, not contract — it exists
/// so the aggregate can be folded without a second search — and the aggregate itself is a
/// resolver rather than a record field, so a client that never asks for it never pays for
/// the marking query.
///
/// <para><b>Individual hits already carry their own marking</b> and no field was added for
/// it: a <c>SearchHit</c> resolves <c>page</c> through <see cref="PageByIdDataLoader"/>,
/// and <c>page { marking { label } }</c> is the same object-level-authorized route every
/// other Page field takes (§6.7). Projecting a marking onto <c>SearchHit</c> itself would
/// be an unauthorized copy, exactly what <see cref="SearchHitType"/> ignores
/// Title/SpaceKey to avoid.</para>
/// </summary>
public sealed class SearchConnectionType : ObjectType<SearchConnection>
{
    protected override void Configure(IObjectTypeDescriptor<SearchConnection> descriptor)
    {
        descriptor.Ignore(c => c.HitPageIds);

        // Nullable on purpose: a search with no hits has nothing to mark, and the honest
        // answer to "what classification is this empty list" is no label rather than
        // OFFICIAL (which would assert a judgement about content that does not exist).
        // See AggregateMarkingLabel's doc.
        descriptor.Field("aggregateMarking")
            .Type<ObjectType<AggregateMarkingLabel>>()
            .ResolveWith<SearchConnectionFieldResolvers>(r => r.GetAggregateMarkingAsync(default!, default!, default!, default));
    }
}

public sealed class SearchConnectionFieldResolvers
{
    /// <summary>
    /// design.md §21.13: the marking a results view may label <i>itself</i> with — the
    /// highest classification among the hits, with the caveats of all of them.
    ///
    /// <para><b>Scope: the whole permission-filtered hit set this connection reports</b>
    /// (the set <c>totalCount</c> counts), not just the twenty edges in this page. Two
    /// reasons, and the first is the load-bearing one:</para>
    /// <list type="number">
    /// <item>The SPA appends pages of results into one list. A per-page aggregate would
    /// force the client to <i>combine</i> aggregates as the user loads more — which is the
    /// caveat-conjunction rule, reimplemented in TypeScript, which §21.1 forbids for
    /// exactly this class of reason. Here every page of the connection reports the same
    /// label and the client renders it verbatim.</item>
    /// <item>A label that drops as you scroll is worse than one that is stable: it would
    /// tell a user the results got less sensitive when all that happened is that they
    /// paged past the sensitive ones.</item>
    /// </list>
    ///
    /// <para><b>Nothing the caller was not shown contributes.</b> The hit set arrives
    /// already permission-filtered inside <c>ISearchService</c> (§6.7/§9.3) — a page above
    /// the caller's clearance, or behind a restriction they fail, is absent from it, so it
    /// cannot raise this label. That is verified by test rather than assumed. The cap at
    /// <c>MaxSearchResults</c> applies here too: this labels the result set the connection
    /// reports, no more and no less.</para>
    ///
    /// <para>Resolution rides <see cref="PageMarkingByPageIdDataLoader"/>, so a query
    /// asking for both this and <c>edges { node { page { marking } } }</c> costs one
    /// PageMarkings query in total — and both render from the same loaded values, so the
    /// aggregate can never disagree with the badges beneath it.</para>
    /// </summary>
    public async Task<AggregateMarkingLabel?> GetAggregateMarkingAsync(
        [Parent] SearchConnection connection,
        PageMarkingByPageIdDataLoader markingLoader,
        [Service] SelectorCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (connection.HitPageIds.Count == 0)
        {
            return null;
        }

        var markings = await markingLoader.LoadAsync(connection.HitPageIds, cancellationToken);
        return AggregateMarkingLabel.Of(markings.Select(m => m ?? ProtectiveMarking.FailClosed), catalog);
    }
}
