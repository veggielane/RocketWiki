using System.Text;
using System.Text.Json;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The `search` connection shape is the frontend's contract
/// (web/src/graphql/operations/search.graphql: totalCount / pageInfo{hasNextPage,
/// endCursor} / edges{cursor, node}), which is why these are hand-rolled records
/// rather than Hot Chocolate's [UsePaging]: the shipped operation passes exactly
/// (query, spaceKey, labels, after) — no first/last/before — and expects a
/// non-null connection, neither of which the paging middleware's defaults produce.
/// Cursors are opaque positions into the permission-filtered hit sequence.
/// </summary>
public sealed record SearchConnection(int TotalCount, IReadOnlyList<SearchEdge> Edges, SearchPageInfo PageInfo)
{
    public static SearchConnection Empty { get; } = new(0, [], new SearchPageInfo(false, null));
}

public sealed record SearchEdge(string Cursor, SearchHit Node);

/// <summary>Named SearchPageInfo (not PageInfo) only to avoid colliding with the relay PageInfo type the auditEvents connection already exports; the fields the SPA reads are identical.</summary>
public sealed record SearchPageInfo(bool HasNextPage, string? EndCursor);

public partial class Query
{
    /// <summary>One "load more" page in the SPA. Server-fixed: the shipped operation deliberately has no client-controlled page size.</summary>
    private const int SearchPageSize = 20;

    /// <summary>
    /// Cap on visible hits materialized per search (so totalCount saturates here).
    /// An honest cap, stated rather than hidden: an exact permission-filtered total
    /// would mean running canView over every candidate in the database for a count
    /// nobody scrolls to, and a count computed any cheaper way (pre-filter) would
    /// leak restricted pages into the number (design.md §6.7).
    /// </summary>
    private const int MaxSearchResults = 100;

    private const string CursorPrefix = "search:";

    /// <summary>
    /// design.md §8/§9: keyword search, permission-filtered in the service layer -
    /// a page the caller can't view is absent from hits, snippets, and totalCount
    /// alike (§6.7 "absent, never forbidden"; over-fetch before filtering per §9.3).
    /// Anonymous callers get an empty connection, same convention as every other
    /// read root. Audited as `search.query` (§7) - see RecordSearchAuditAsync.
    /// </summary>
    [AuditAction("search.query")]
    [UseAuditDispatch]
    public async Task<SearchConnection> Search(
        string query,
        string? spaceKey,
        string[]? labels,
        string? after,
        [Service] ISearchService searchService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return SearchConnection.Empty;
        }

        var hits = await searchService.SearchAsync(
            new SearchRequest(query, spaceKey, labels), principal, MaxSearchResults, cancellationToken);

        await RecordSearchAuditAsync(auditSink, query, spaceKey, labels, hits.Count, cancellationToken);

        var start = DecodeAfter(after);
        var edges = hits
            .Skip(start)
            .Take(SearchPageSize)
            .Select((hit, i) => new SearchEdge(EncodeCursor(start + i), hit))
            .ToList();

        return new SearchConnection(
            hits.Count,
            edges,
            new SearchPageInfo(
                HasNextPage: start + edges.Count < hits.Count,
                EndCursor: edges.Count > 0 ? edges[^1].Cursor : null));
    }

    /// <summary>
    /// design.md §7's event model lists "search query text" as an explicit example of
    /// the Details JSON column - so the audit row DOES carry the raw query text, by
    /// design: the audit table is the grant-protected, append-only record of who
    /// searched for what, and probing (searching for restricted terms) is exactly the
    /// signal it exists to keep. §15 draws the line elsewhere: the same text must
    /// never reach telemetry - which it doesn't; DbAuditSink's metric tags are the
    /// bounded action/outcome/channel vocabulary, DetailsJson stays in the table, and
    /// TelemetryHygieneTests plants a sentinel through this exact path to prove it.
    ///
    /// Recorded explicitly (rather than left to AuditFieldMiddleware's automatic
    /// dispatch) because the middleware can't know the query text belongs in Details;
    /// the middleware's own later record for this field dedupes against this one
    /// inside DbAuditSink, so the request still writes exactly one search.query row.
    /// </summary>
    private static Task RecordSearchAuditAsync(
        IAuditSink auditSink, string query, string? spaceKey, string[]? labels, int resultCount, CancellationToken ct) =>
        auditSink.RecordAsync(
            new AuditRecord(
                "search.query",
                AuditOutcome.Success,
                SpaceKey: spaceKey,
                DetailsJson: JsonSerializer.Serialize(new { query, spaceKey, labels, resultCount })),
            ct);

    private static string EncodeCursor(int index) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(CursorPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>The index to start from: the position after the `after` cursor, or 0. Cursors are opaque to clients, so a malformed one is treated as "from the top" rather than an error - defensive, and what a stale bookmark deserves.</summary>
    private static int DecodeAfter(string? after)
    {
        if (string.IsNullOrEmpty(after))
        {
            return 0;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(after));
            if (decoded.StartsWith(CursorPrefix, StringComparison.Ordinal)
                && int.TryParse(decoded[CursorPrefix.Length..], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var index))
            {
                return index + 1;
            }
        }
        catch (FormatException)
        {
            // Not base64 - fall through to "from the top".
        }

        return 0;
    }
}
