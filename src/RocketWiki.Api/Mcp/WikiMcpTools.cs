using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Api.Markings;
using RocketWiki.Api.Reads;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data;

namespace RocketWiki.Api.Mcp;

/// <summary>
/// design.md §8's MCP v1 tool set — read-only, deliberately (§17: write tools are held
/// back until there's a concrete, reviewed need). Every tool here obeys three rules:
///
/// 1. <b>Same code path as GraphQL.</b> Tools call the identical services the resolvers
///    call (<see cref="IPageReadService"/>, <see cref="ISearchService"/>,
///    <see cref="SpaceReads"/>) and never touch EF directly, so object-level
///    authorization (§6.7) is inherited, not reimplemented. There is no parallel,
///    subtly-different read path to keep honest.
///
///    <para><b>One deliberate difference, stated because it looks like a violation:</b>
///    the search tool projects each hit's title, space key and snippet straight off the
///    <see cref="ISearchService"/> record, while GraphQL's <c>SearchHitType</c> ignores
///    exactly those fields and forces clients through <c>page { … }</c>. That rule exists
///    because in a GraphQL response the same page can be reachable through several paths,
///    so page data must flow through Page resolvers rather than around them. MCP has no
///    resolver graph and no sub-selection: the payload IS the response, there is no
///    second path to be inconsistent with, and every field here was computed by
///    SearchService strictly after canView passed for that page. Routing it through the
///    page reader a second time would buy a duplicate check, not a missing one. The gate
///    is in SearchService; this is the one place to look for it.</para>
/// 2. <b>Absent, never forbidden (§6.7).</b> A page or space the caller can't view
///    produces a result byte-identical to one that doesn't exist —
///    <see cref="PageNotFoundMessage"/>/<see cref="SpaceNotFoundMessage"/> are constants
///    so the two cases cannot even accidentally diverge. Constants also mean nothing
///    content-derived ever rides an error (relevant to §15: the MCP SDK copies tool
///    error text into its activity status).
/// 3. <b>Declared audit (§7).</b> Every tool carries <see cref="AuditActionAttribute"/>
///    (or <see cref="NoAuditAttribute"/> with a reason); AuditCoverageTests fails the
///    build otherwise, and <see cref="McpServerConfiguration"/>'s call-tool filter
///    refuses to execute a registered tool that has no declaration. Emission happens in
///    that filter — tools only contribute the subject/details via
///    <see cref="McpAuditState"/>, mirroring how GraphQL's AuditFieldMiddleware
///    describes subjects for [UseAuditDispatch] fields.
///
/// Tool names are set explicitly (never derived from the method name) so the audit
/// registry, the SDK's tool list, and design.md §8's published names cannot drift —
/// enforced by AuditCoverageTests.
///
/// 4. <b>Marked payloads (§21.13).</b> Every payload that carries page text carries the
///    page's protective marking with it, and a multi-item payload carries the aggregate
///    over its items. This is not decoration: <b>an MCP client is typically an LLM</b>,
///    and an unmarked payload is precisely how a paragraph of UK SECRET ends up
///    summarised into a document nobody classified. The label is
///    <c>ProtectiveMarking.Format</c>'s output — the same string the SPA and an audit
///    reviewer see (§21.1's one-formatter rule) — never a second rendering.
///
/// Identity: tools resolve the ABAC <see cref="Principal"/> from
/// <see cref="ICurrentPrincipalAccessor"/> — the validated token of THIS request, never
/// a service account and never the local User mirror (§6.1, §11). In stateless HTTP
/// mode every tool call is its own authenticated request, so "acts as the token's user"
/// holds per call, not per session.
/// </summary>
[McpServerToolType]
public sealed class WikiMcpTools
{
    /// <summary>One constant for both "doesn't exist" and "not permitted to view" —
    /// design.md §6.7's leak-proofing depends on these being indistinguishable.</summary>
    internal const string PageNotFoundMessage = "Page not found.";

    /// <inheritdoc cref="PageNotFoundMessage"/>
    internal const string SpaceNotFoundMessage = "Space not found.";

    internal const string AuthenticationRequiredMessage = "Authentication required.";

    private const int MaxSearchResults = 25;
    private const int DefaultSearchResults = 10;

    [McpServerTool(Name = "search", Title = "Search the wiki", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [AuditAction("search.query")]
    [Description("Full-text search over wiki pages. Only pages the calling user is permitted to view are " +
        "searched or returned; a restricted page is simply absent from the results.")]
    public static async Task<McpSearchResult> SearchAsync(
        [Description("The text to search for in page titles and content.")] string query,
        ISearchService searchService,
        IPageMarkingReader markingReader,
        ICurrentPrincipalAccessor principalAccessor,
        McpAuditState auditState,
        CancellationToken cancellationToken,
        [Description("Optional space key to restrict the search to a single space.")] string? spaceKey = null,
        [Description("Maximum number of results to return (1-25).")] int limit = DefaultSearchResults)
    {
        var principal = RequirePrincipal(principalAccessor);

        limit = Math.Clamp(limit, 1, MaxSearchResults);
        var hits = await searchService.SearchAsync(
            new SearchRequest(query, spaceKey, Labels: null), principal, limit, cancellationToken);

        // design.md §7: the search query text belongs in the audit row's Details — the
        // audit table is the regulated record and the sanctioned home for content-ish
        // detail (it must NOT go to telemetry, §15).
        //
        // The shape matches Query.Search's row EXACTLY, `labels` included. One action name
        // must mean one Details shape: AnalyticsService.BuildSearchesAsync reads
        // `resultCount` to decide whether a search found nothing, so an MCP-flavoured
        // `results` key made every MCP search invisible to the zero-result panel while
        // still counting in the top-terms one. A reader of the audit table should never
        // have to know which channel wrote a row to parse it.
        //
        // `labels` is written as null rather than omitted, because this comment claimed
        // an exact match while the key was simply absent — and absent and null are
        // different answers to "did this search filter by label". Null is the true one:
        // this tool has no labels argument, so no filter was applied. A future reader
        // querying Details for label usage would otherwise have to know that MCP rows
        // are shaped differently, which is the thing this row exists not to require.
        auditState.SetDetails(System.Text.Json.JsonSerializer.Serialize(
            new { query, spaceKey, labels = (string[]?)null, resultCount = hits.Count }));

        // §21.13: per-hit markings and an aggregate over exactly the hits being returned
        // — which are already permission-filtered, so nothing the caller cannot view can
        // contribute to either. One query for the batch.
        var markings = await markingReader.LoadAsync(
            hits.Select(h => h.PageId).Distinct().ToArray(), cancellationToken);

        return new McpSearchResult(
            AggregateMarkingLabel.Of(hits.Select(h => MarkingFor(markings, h.PageId)))?.Label,
            hits.Select(h => new McpSearchHit(
                h.PageId, h.Title, h.SpaceKey, MarkingFor(markings, h.PageId).Format(), h.Snippet)).ToList());
    }

    [McpServerTool(Name = "get_page", Title = "Read a wiki page", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [AuditAction("page.view")]
    [Description("Reads a single wiki page by id, returning its content as Markdown. " +
        "Returns an error if no such page exists.")]
    public static async Task<McpPage> GetPageAsync(
        [Description("The page id (a GUID, as returned by search, get_page_tree, or list_spaces).")] string pageId,
        IPageReadService readService,
        IPageMarkingReader markingReader,
        ICurrentPrincipalAccessor principalAccessor,
        IAuditSink auditSink,
        McpAuditState auditState,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(principalAccessor);

        // An unparseable id can't name an existing page, so it takes the exact same
        // "not found" exit as a missing one — not a distinct validation error, and
        // never an exception that would echo the input back (§15).
        if (!Guid.TryParse(pageId, out var id))
        {
            throw new McpException(PageNotFoundMessage);
        }

        var result = await readService.GetPageAsync(id, principal, cancellationToken);
        if (result is ReadResult<Page>.Denied denied)
        {
            // §6.7's internal result distinguishes a denial exactly long enough to
            // audit it with its failing restriction (§7), same as Query.Page; the
            // constant error below then restores wire-level indistinguishability.
            await ReadDenialAudit.RecordAsync(
                auditSink, "page.view", AuditSubjectType.Page, id, denied.Reason, cancellationToken);
        }

        if (result.ValueOrNull() is not { } page)
        {
            throw new McpException(PageNotFoundMessage);
        }

        auditState.SetSubject(AuditSubjectType.Page, page.Id);

        // §21.13: the marking rides with the content, resolved only after canView passed
        // — so this is the marking of a page the caller has just been permitted to read,
        // and stating it is stating why they were let in.
        var markings = await markingReader.LoadAsync([page.Id], cancellationToken);

        return new McpPage(
            page.Id, page.Title, MarkingFor(markings, page.Id).Format(), page.Slug, page.SpaceId,
            page.CurrentRevisionNumber, page.UpdatedAtUtc, page.CurrentContent);
    }

    [McpServerTool(Name = "list_spaces", Title = "List wiki spaces", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [AuditAction("space.browse")]
    [Description("Lists the wiki spaces the calling user can view. Spaces the user holds no role in are absent.")]
    public static async Task<McpSpaceList> ListSpacesAsync(
        RocketWikiDbContext db,
        ICurrentPrincipalAccessor principalAccessor,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(principalAccessor);

        // SpaceReads is the same visibility logic Query.Spaces uses — see its doc for
        // why space reads live at the API layer (no Core space read service yet).
        var spaces = await SpaceReads.GetViewableSpacesAsync(db, principal, cancellationToken);
        return new McpSpaceList(spaces.Select(s => new McpSpace(s.Id, s.Key, s.Name)).ToList());
    }

    [McpServerTool(Name = "get_page_tree", Title = "Browse a space's page tree", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [AuditAction("space.browse")]
    [Description("Returns the page tree of a space by space key, pruned to pages the calling user can view. " +
        "Returns an error if no such space exists.")]
    public static async Task<McpPageTree> GetPageTreeAsync(
        [Description("The space key, as returned by list_spaces.")] string spaceKey,
        RocketWikiDbContext db,
        IPageReadService readService,
        ICurrentPrincipalAccessor principalAccessor,
        IAuditSink auditSink,
        McpAuditState auditState,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(principalAccessor);

        // Same audit split as GraphQL's Query.Space over the same SpaceReads seam
        // (design.md §7/§8): a specific-space lookup that fails visibility is a
        // refused browse, recorded as Denied with the no-space-role reason - then the
        // constant error restores wire-level indistinguishability from a missing key.
        var spaceResult = await SpaceReads.GetViewableSpaceByKeyAsync(db, principal, spaceKey, cancellationToken);
        if (spaceResult is SpaceReadResult.Denied spaceDenied)
        {
            await ReadDenialAudit.RecordAsync(
                auditSink, "space.browse", AuditSubjectType.Space, spaceDenied.SpaceId, spaceDenied.Reason, cancellationToken);
        }

        if (spaceResult is not SpaceReadResult.Found { Space: var space })
        {
            throw new McpException(SpaceNotFoundMessage);
        }

        // The tree arrives pre-pruned (design.md §6.7): a node failing canView is
        // dropped with its whole subtree inside IPageReadService, never filtered here.
        // A Denied here is a refused browse (no-space-role), audited like
        // Query.PageTree; pruning inside a permitted browse is deliberately not a
        // denial. Both collapse to the same empty tree the caller can't tell apart.
        var treeResult = await readService.GetPageTreeAsync(space.Id, principal, cancellationToken);
        if (treeResult is ReadResult<IReadOnlyList<PageTreeNode>>.Denied denied)
        {
            await ReadDenialAudit.RecordAsync(
                auditSink, "space.browse", AuditSubjectType.Space, space.Id, denied.Reason, cancellationToken);
        }

        var tree = treeResult.ValueOrNull() ?? [];

        auditState.SetSubject(AuditSubjectType.Space, space.Id, space.Key);

        // §21.13: each surviving node's own marking, plus the aggregate over the whole
        // pruned tree. No query at all — PageTreeNode already carries the marking the
        // walk GATED on (§21.9), so the payload cannot show one marking while pruning
        // used another, and the constant-query budget is untouched.
        return new McpPageTree(
            space.Key,
            space.Name,
            AggregateMarkingLabel.Of(Flatten(tree).Select(n => n.Marking.ToMarking()))?.Label,
            tree.Select(ToNode).ToList());
    }

    /// <summary>
    /// Fail closed (§6): an authenticated request whose token yields no usable ABAC
    /// principal can view nothing — surfaced as a constant, content-free error. The
    /// endpoint's RequireAuthorization plus the call-tool filter already reject
    /// anonymous callers before any tool runs; this is the in-tool backstop.
    /// </summary>
    private static Principal RequirePrincipal(ICurrentPrincipalAccessor accessor) =>
        accessor.Current ?? throw new McpException(AuthenticationRequiredMessage);

    private static McpPageTreeNode ToNode(PageTreeNode node) =>
        new(node.Id, node.Title, node.Marking.Label, node.Slug, node.Children.Select(ToNode).ToList());

    /// <summary>Every node in the pruned tree, at every depth — the aggregate covers what
    /// the payload actually contains, not just its top level.</summary>
    private static IEnumerable<PageTreeNode> Flatten(IEnumerable<PageTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>The reader fills every requested key, so the fallback is unreachable — but
    /// the one honest answer for a page whose marking row went missing is the same TOP
    /// SECRET every other read path substitutes (§21.5), never an unmarked payload.</summary>
    private static ProtectiveMarking MarkingFor(
        IReadOnlyDictionary<Guid, ProtectiveMarking> markings, Guid pageId) =>
        markings.GetValueOrDefault(pageId) ?? ProtectiveMarking.FailClosed;
}

/// <summary>Results are plain records; the SDK serializes them into a JSON text content
/// block. Content is Markdown (design.md §8: the API serves Markdown, not HTML).
///
/// <para><c>Marking</c> on a content-bearing payload, and <c>AggregateMarking</c> on a
/// multi-item one, are design.md §21.13. Both are rendered strings from the single
/// server-side formatter (<c>UK SECRET [GB EYES ONLY]</c>) rather than structured parts:
/// the consumer is usually a language model, the marking has to travel with the text as
/// text, and a client that reassembled the parts itself would be the second renderer
/// §21.1 forbids. <c>AggregateMarking</c> is null only when the payload has no items —
/// nothing shown, nothing to mark.</para></summary>
public sealed record McpPage(
    Guid Id, string Title, string Marking, string Slug, Guid SpaceId, int Revision, DateTime UpdatedAtUtc,
    string Markdown);

public sealed record McpSearchResult(string? AggregateMarking, IReadOnlyList<McpSearchHit> Hits);

public sealed record McpSearchHit(Guid PageId, string Title, string SpaceKey, string Marking, string Snippet);

/// <summary>No marking: a space is not marked, its pages are (§21 marks pages). Listing
/// spaces reveals no page content, so there is nothing here to label.</summary>
public sealed record McpSpaceList(IReadOnlyList<McpSpace> Spaces);

public sealed record McpSpace(Guid Id, string Key, string Name);

public sealed record McpPageTree(
    string SpaceKey, string SpaceName, string? AggregateMarking, IReadOnlyList<McpPageTreeNode> Pages);

public sealed record McpPageTreeNode(
    Guid Id, string Title, string Marking, string Slug, IReadOnlyList<McpPageTreeNode> Children);
