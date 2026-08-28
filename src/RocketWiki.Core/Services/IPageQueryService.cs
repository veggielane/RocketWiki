using RocketWiki.Core.Access;
using RocketWiki.Core.Query;

namespace RocketWiki.Core.Services;

/// <summary>
/// The result of running one RQL query (design.md §22): either the ordered ids of the
/// pages the caller may see, or the reasons the query would not parse. Never both.
///
/// <para><b>Ids only, and no count of anything else.</b> <see cref="PageIds"/> is the
/// permission-filtered set, in query order, capped; the API resolves each one back to a
/// <c>Page</c> through the object-level-authorized loader rather than being handed
/// projected page data (design.md §6.7/§8 — a projected title is an unauthorized copy).
/// Nothing here reports how many candidates were considered, how many were filtered out,
/// or whether the candidate cap was reached: every one of those numbers would imply the
/// existence of pages the caller may not know exist (§6.7).</para>
/// </summary>
public sealed record PageQueryOutcome(IReadOnlyList<Guid> PageIds, IReadOnlyList<RqlError> Errors)
{
    public static PageQueryOutcome Empty { get; } = new([], []);

    public static PageQueryOutcome Invalid(IReadOnlyList<RqlError> errors) => new([], errors);

    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Runs an RQL query (design.md §22) and returns the pages the caller may view.
///
/// <para>Behind an interface for the reason every read service is: the implementation lives
/// in RocketWiki.Data with the permission loader, and the API layer holds no query
/// construction of its own. The permission filter is a per-page post-filter over candidates,
/// exactly as search and <c>GetPagesByLabelAsync</c> do it — the query chooses candidates and
/// has no influence whatsoever on which check runs against them.</para>
/// </summary>
public interface IPageQueryService
{
    /// <param name="maxResults">Hard cap on visible results (design.md §22's stated cap).</param>
    Task<PageQueryOutcome> ExecuteAsync(
        string query, Principal principal, int maxResults, CancellationToken cancellationToken = default);
}
