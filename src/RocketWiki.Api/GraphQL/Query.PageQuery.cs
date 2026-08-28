using System.Text.Json;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Query;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The <c>pageQuery</c> connection. Same hand-rolled shape as <see cref="SearchConnection"/>
/// (totalCount / pageInfo / edges{cursor,node}) rather than Hot Chocolate's
/// <c>[UsePaging]</c>, for the same reason: the connection is non-null, the page size is
/// server-bounded, and the aggregate marking needs the whole filtered id set rather than one
/// page of edges.
///
/// <para><c>Errors</c> is the one addition to search's shape. An unparseable RQL string is
/// <b>not</b> a server error: the query is authored content — it lives in a Markdown fence
/// on a page — so the widget rendering it has to show the author what is wrong and where,
/// which a GraphQL top-level error cannot do positionally. A valid query returns an empty
/// list here.</para>
/// </summary>
public sealed record PageQueryConnection(
    int TotalCount,
    IReadOnlyList<PageQueryEdge> Edges,
    PageQueryPageInfo PageInfo,
    IReadOnlyList<RqlErrorView> Errors,
    IReadOnlyList<Guid> MatchedPageIds)
{
    public static PageQueryConnection Empty { get; } = new(0, [], new PageQueryPageInfo(false, null), [], []);

    public static PageQueryConnection Invalid(IReadOnlyList<RqlError> errors) =>
        new(0, [], new PageQueryPageInfo(false, null), errors.Select(RqlErrorView.From).ToList(), []);
}

public sealed record PageQueryEdge(string Cursor, PageQueryRow Node);

/// <summary>
/// One matched page, carrying <b>only</b> its id.
///
/// <para>Nothing about the page is projected onto this row — no title, no space key, no
/// marking — for the reason <see cref="SearchHitType"/> spells out: a projected copy is an
/// unauthorized copy that routes around the object-level <c>Page</c> resolvers every other
/// path serves page data through (design.md §6.7/§8). The row's <c>page</c> field goes
/// through <see cref="PageByIdDataLoader"/> like everything else.</para>
/// </summary>
public sealed record PageQueryRow(Guid PageId);

/// <summary>Named PageQueryPageInfo (not PageInfo) to avoid colliding with the relay PageInfo the auditEvents connection exports; the fields are the ones every connection here exposes.</summary>
public sealed record PageQueryPageInfo(bool HasNextPage, string? EndCursor);

public partial class Query
{
    /// <summary>Default edges per request when the caller names no <c>first</c>.</summary>
    private const int DefaultPageQueryPageSize = 20;

    /// <summary>
    /// Cap on visible results materialized per query, and therefore where
    /// <c>totalCount</c> saturates. An honest cap, stated rather than hidden — the same
    /// reasoning <c>Query.Search</c> gives: an exact permission-filtered total would mean
    /// running canView over every candidate in the database for a number nobody scrolls to,
    /// and any cheaper count would be computed before the filter and would therefore leak
    /// restricted pages into it (design.md §6.7).
    /// </summary>
    private const int MaxPageQueryResults = 100;

    private const string PageQueryCursorPrefix = "rql:";

    /// <summary>
    /// design.md §22: runs an RQL query and returns the pages the caller may view.
    ///
    /// <para><b>The RQL string decides which candidates are considered; it never decides
    /// which permission check runs.</b> Every candidate is post-filtered through the ordinary
    /// per-page <c>canView</c> gate — space role, the page's own and every ancestor's
    /// restrictions, and the protective marking (design.md §21) — in
    /// <c>IPageQueryService</c>. A page the caller cannot view is absent: not a redacted row,
    /// not a gap in the ordering, and not implied by <c>totalCount</c>, which counts visible
    /// results only (§6.7).</para>
    ///
    /// <para><b>An invisible space is indistinguishable from a nonexistent one.</b>
    /// <c>space = "BLACKPROJECT"</c> returns exactly what a key naming no space returns:
    /// an empty connection, no error, no hint. The same holds for an unknown label and an
    /// unknown creator. Validation errors are about syntax and vocabulary only.</para>
    ///
    /// <para>Anonymous callers get an empty connection, the same convention as every other
    /// read root. Audited as <c>page.query</c> (§7) with the query text in Details, exactly
    /// as <c>search.query</c> carries its own — the audit table is where who-asked-what
    /// lives, and §15 keeps that same text out of every span, log and metric tag.</para>
    /// </summary>
    /// <param name="query">The RQL string. See design.md §22 for the grammar and the closed field set.</param>
    /// <param name="first">
    /// Edges to return, clamped to 1..<see cref="MaxPageQueryResults"/>. Never widens the
    /// underlying result cap.
    /// </param>
    /// <param name="after">An opaque cursor from a previous page's <c>endCursor</c>.</param>
    [AuditAction("page.query")]
    public async Task<PageQueryConnection> PageQuery(
        string query,
        int? first,
        string? after,
        [Service] IPageQueryService pageQueryService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return PageQueryConnection.Empty;
        }

        var outcome = await pageQueryService.ExecuteAsync(
            query, principal, MaxPageQueryResults, cancellationToken);

        await RecordPageQueryAuditAsync(auditSink, query, outcome, cancellationToken);

        if (!outcome.IsValid)
        {
            return PageQueryConnection.Invalid(outcome.Errors);
        }

        var pageSize = first is null
            ? DefaultPageQueryPageSize
            : Math.Clamp(first.Value, 1, MaxPageQueryResults);

        var start = PositionalCursor.DecodeAfter(PageQueryCursorPrefix, after);
        var edges = outcome.PageIds
            .Skip(start)
            .Take(pageSize)
            .Select((pageId, i) => new PageQueryEdge(
                PositionalCursor.Encode(PageQueryCursorPrefix, start + i), new PageQueryRow(pageId)))
            .ToList();

        return new PageQueryConnection(
            outcome.PageIds.Count,
            edges,
            new PageQueryPageInfo(
                HasNextPage: start + edges.Count < outcome.PageIds.Count,
                EndCursor: edges.Count > 0 ? edges[^1].Cursor : null),
            [],
            // The permission-filtered id set this connection reports - exactly what
            // totalCount counts - carried for the aggregate marking label and never exposed
            // as a field. See PageQueryConnectionType.
            outcome.PageIds);
    }

    /// <summary>
    /// design.md §7: one row per executed query, with the raw RQL text in Details — the same
    /// decision <c>search.query</c> makes, and for the same reason: the audit table is the
    /// grant-protected, append-only record of who asked what, and a query probing for
    /// restricted terms is precisely the signal it exists to keep. §15 draws the line
    /// elsewhere and the RQL string reaches no span, log or metric tag; the hygiene tests
    /// plant a sentinel through this exact path to prove it.
    ///
    /// <para>Recorded explicitly rather than by <c>[UseAuditDispatch]</c>, and keyed for
    /// dedup on the query text, so two <c>pageQuery</c> fields in one document produce two
    /// rows rather than one — the same reasoning <c>gitlab.fetch</c> uses for two distinct
    /// resources in one request. Widget-heavy pages will do exactly that.</para>
    ///
    /// <para>An unparseable query is audited too. The caller still asked, the text is still
    /// the interesting artefact, and <c>Outcome</c> stays <c>Success</c>: <c>denied</c> is
    /// reserved for ABAC refusals, and RQL never produces one — restricted pages are absent
    /// from the candidate set rather than refused (§6.7), and any mid-flight denial audits as
    /// an ordinary page read where it happens.</para>
    /// </summary>
    private static Task RecordPageQueryAuditAsync(
        IAuditSink auditSink, string query, PageQueryOutcome outcome, CancellationToken ct) =>
        auditSink.RecordAsync(
            new AuditRecord(
                "page.query",
                AuditOutcome.Success,
                DetailsJson: JsonSerializer.Serialize(new
                {
                    query,
                    valid = outcome.IsValid,
                    resultCount = outcome.PageIds.Count,
                }),
                DedupKey: query),
            ct);
}
