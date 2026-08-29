using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// Every entry of one collection on one page that this caller may see, oldest first
    /// (docs/ENTRIES-AND-FORMS-PLAN.md).
    ///
    /// <para>Null for a page that does not exist and for one the caller cannot view — the
    /// same answer for both, per §6.7. An EMPTY LIST is returned both when the collection
    /// holds nothing and when it holds nothing this caller may read, and those two must
    /// stay indistinguishable: a count of what was pruned would be a census of the
    /// classified estate, which is the same argument that keeps classification out of RQL
    /// (§22.3) and out of every telemetry dimension (§21.8).</para>
    /// </summary>
    [AuditAction("page.entry.list")]
    [UseAuditDispatch]
    public async Task<IReadOnlyList<PageEntryView>?> PageEntries(
        Guid pageId,
        string collection,
        [Service] IPageEntryService entryService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            // The absent shape every read root gives an anonymous caller.
            return null;
        }

        var result = await entryService.ListAsync(pageId, collection, principal, cancellationToken);
        return result is ReadResult<IReadOnlyList<PageEntryView>>.Found found ? found.Value : null;
    }
}
