using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Services;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed <see cref="IPageMarkingReader"/> — the display-side batch read of protective
/// markings (design.md §21.13). Read the interface's doc first: it says what this may not
/// become.
///
/// <para>Why a service rather than another inline query: before this existed, three call
/// sites (the <c>Page.marking</c> DataLoader, the MCP tools, the assistant) each wanted
/// "the markings of these page ids, countries included, missing rows as FailClosed", and
/// the third and fourth copies of that are how one of them ends up forgetting the
/// FailClosed substitution and rendering a blank badge for a page whose marking row went
/// missing. One implementation, one substitution.</para>
///
/// <para>It is deliberately NOT built on <c>PermissionContextLoader</c>, whose batch
/// marking load looks identical: that loader assembles <i>authorization inputs</i> in a
/// contractual order for the calculator, and widening its surface for a display caller is
/// how a display caller ends up one refactor away from a decision. The queries look the
/// same because the table is the same; the two paths stay separate because their purposes
/// are not.</para>
///
/// <para><c>AsNoTracking</c> unconditionally: every caller is a pure read model, and a
/// marking row is never mutated through this path.</para>
/// </summary>
public sealed class PageMarkingReadService(RocketWikiDbContext db) : IPageMarkingReader
{
    public async Task<IReadOnlyDictionary<Guid, ProtectiveMarking>> LoadAsync(
        IReadOnlyCollection<Guid> pageIds, CancellationToken cancellationToken = default)
    {
        var ids = pageIds as Guid[] ?? pageIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, ProtectiveMarking>();
        }

        // ONE query for the whole batch — the country rows ride along on the collection
        // include rather than being fetched per page. A results page or a whole space's
        // tree can be hundreds of ids.
        var rows = await db.PageMarkings
            .AsNoTracking()
            .Include(m => m.Countries)
            .Include(m => m.Selectors)
            .Where(m => ids.Contains(m.PageId))
            .ToListAsync(cancellationToken);

        var byPageId = rows.ToDictionary(m => m.PageId, m => m.ToMarking());
        foreach (var id in ids)
        {
            // The interface's guarantee: every requested id present, a missing row
            // substituted with TOP SECRET (§21.5) rather than left absent for a consumer
            // to interpret as "unmarked".
            byPageId.TryAdd(id, ProtectiveMarking.FailClosed);
        }

        return byPageId;
    }
}
