using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Batches per-page protective-marking resolution: ONE PageMarkings query (with its
/// country rows) for every page id in the batch — how <c>Page.marking</c> resolves for a
/// single page and for a list of them alike, without an N+1 (design.md §8's DataLoader
/// rule). The query itself lives in <see cref="IPageMarkingReader"/>, shared with the
/// paths that have no DataLoader to reach for (the MCP tools, the assistant), so the
/// display side has one implementation and one fail-closed substitution.
///
/// <para>No authorization decision here, and the reasoning is the labels/properties one
/// (§6.4.2/§20/§21): every page id reaching this loader belongs to a <c>Page</c> that
/// already passed canView — which now <i>includes</i> the clearance gate — to be
/// resolvable at all. Displaying a marking to someone already cleared for the page
/// reveals nothing they were not entitled to; the marking is not a second secret, it is
/// the reason they were let in.</para>
///
/// <para>Yields <see cref="ProtectiveMarking"/> — the value object, not the
/// <c>PageMarking</c> entity, which carries a <c>Page</c> navigation whose exposure would
/// open a Page-shaped route around object-level authorization (the same reason
/// <c>LabelRef</c> and <c>PagePropertyValue</c> exist). The GraphQL field maps it to
/// <c>PageMarkingView</c> at the resolver; the value object is what comes out of here
/// because the connection-level aggregate label (§21.13) folds real markings, and
/// re-deriving them from views would be a round trip through the display shape for no
/// reason.</para>
///
/// <para>A page with no marking row resolves to <see cref="ProtectiveMarking.FailClosed"/>
/// inside the reader, matching every other read path, so the rendered label reads TOP
/// SECRET rather than blank. A blank would be the one answer that misrepresents the
/// enforcement the caller is actually subject to.</para>
/// </summary>
public sealed class PageMarkingByPageIdDataLoader(
    DbContextOptions<RocketWikiDbContext> dbOptions,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : BatchDataLoader<Guid, ProtectiveMarking>(batchScheduler, options ?? new DataLoaderOptions())
{
    protected override async Task<IReadOnlyDictionary<Guid, ProtectiveMarking>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        // Own context per batch — see DataLoaderDbContext. The reader is constructed
        // here rather than injected precisely because injecting it would bring the
        // request-scoped context back in with it; Program registers exactly this
        // shape (`new PageMarkingReadService(db)`) with no decorator, so this stays
        // faithful to the DI registration.
        await using var db = DataLoaderDbContext.Create(dbOptions);
        var reader = new PageMarkingReadService(db);

        return await reader.LoadAsync(keys, cancellationToken);
    }
}
