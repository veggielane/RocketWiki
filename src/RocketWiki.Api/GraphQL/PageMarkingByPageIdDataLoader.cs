using GreenDonut;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Services;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Batches per-page protective-marking resolution: ONE PageMarkings query (with its
/// country rows) for every page id in the batch — how <c>Page.marking</c> resolves for a
/// single page and for a list of them alike, without an N+1 (design.md §8's DataLoader
/// rule).
///
/// <para>No authorization decision here, and the reasoning is the labels/properties one
/// (§6.4.2/§20/§21): every page id reaching this loader belongs to a <c>Page</c> that
/// already passed canView — which now <i>includes</i> the clearance gate — to be
/// resolvable at all. Displaying a marking to someone already cleared for the page
/// reveals nothing they were not entitled to; the marking is not a second secret, it is
/// the reason they were let in.</para>
///
/// <para>Returns <see cref="ProtectiveMarking.FailClosed"/> for a page with no marking
/// row, matching every other read path, so the rendered label reads TOP SECRET rather
/// than blank. A blank would be the one answer that misrepresents the enforcement the
/// caller is actually subject to.</para>
///
/// <para>Yields <see cref="PageMarkingView"/>, never the <c>PageMarking</c> entity: that
/// carries a <c>Page</c> navigation, and returning it from a read path would open a
/// Page-shaped route around object-level authorization — the same reason
/// <c>LabelRef</c> and <c>PagePropertyValue</c> exist.</para>
/// </summary>
public sealed class PageMarkingByPageIdDataLoader(
    RocketWikiDbContext db,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : BatchDataLoader<Guid, PageMarkingView>(batchScheduler, options ?? new DataLoaderOptions())
{
    private readonly RocketWikiDbContext _db = db;

    protected override async Task<IReadOnlyDictionary<Guid, PageMarkingView>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var rows = await _db.PageMarkings
            .AsNoTracking()
            .Include(m => m.Countries)
            .Where(m => keys.Contains(m.PageId))
            .ToListAsync(cancellationToken);

        var byPageId = rows.ToDictionary(m => m.PageId, m => PageMarkingView.From(m.ToMarking()));
        foreach (var key in keys)
        {
            if (!byPageId.ContainsKey(key))
            {
                byPageId[key] = PageMarkingView.From(ProtectiveMarking.FailClosed);
            }
        }

        return byPageId;
    }
}
