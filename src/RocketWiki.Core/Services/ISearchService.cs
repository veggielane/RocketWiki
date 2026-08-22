using RocketWiki.Core.Access;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §9.1/§9.3/§14: keyword search behind an interface so the SQL Server FTS
/// path and the SQLite LIKE fallback are interchangeable to callers. Results are
/// permission-filtered AFTER over-fetching - the search mechanism returns more
/// candidates than requested, then canView is applied, so a restriction-heavy result
/// set doesn't come back looking empty. Same "absent, not forbidden" rule as every
/// other read: a page the caller can't view simply never appears, not as a stub or a count.
/// </summary>
public interface ISearchService
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchRequest request, Principal principal, int maxResults, CancellationToken cancellationToken = default);
}
