using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using CorePageGraph = RocketWiki.Core.Services.PageGraph;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// The document graph (design.md §6.7 / §21.8): every page the caller may view as a
    /// node, and every <c>page://</c> link between two such pages as an edge — the whole
    /// instance when <paramref name="spaceKey"/> is null, the subgraph induced on one
    /// space when it names one (a link that leaves the space has no node to end on in
    /// the filtered view and is not an edge).
    ///
    /// <para><b>An OMITTING surface, never a disclosing one.</b> A page the caller cannot
    /// view is absent: no placeholder node, no edge to or from it, and no count that
    /// includes it. The tree and <c>Page.linkTargets</c> disclose a denied page as a
    /// placeholder because a reader inside a space needs a name for the gap; a graph of
    /// the instance is a compilation, and a placeholder in a compilation is a census of
    /// what the caller may not read — an edge with one hidden endpoint would be that
    /// census with the name left off. The gate is <see cref="IPageGraphService"/>'s, the
    /// same batched <c>canView</c> walk every read inherits (§21.9); nothing here decides
    /// anything.</para>
    ///
    /// <para><b>Absent, never forbidden.</b> A key naming no live space, an archived
    /// space, and a space no access grant admits the caller to all answer the same empty
    /// graph, indistinguishably (§6.7) — the key is canonicalized first
    /// (<see cref="SpaceKeys.Canonical"/>), so <c>eng</c> scopes to <c>ENG</c> rather than
    /// to nothing. An unknown key never reaches the service: there is no space to decide
    /// about. An anonymous request gets the empty graph without a decision and without a
    /// row, as every read root does.</para>
    ///
    /// <para><b>Audit (§7): one <c>graph.view</c> row per read, always <c>success</c>.</b>
    /// Recorded explicitly (like <c>search.query</c> and <c>page.query</c>) rather than by
    /// <c>[UseAuditDispatch]</c>, so the row can carry the scope and what the compilation
    /// held — the space key asked for, the space's id when it names a live space, and the
    /// node and edge counts of the graph the caller received; keyed for dedup on the scope,
    /// so two <c>pageGraph</c> fields in one document write two rows. <c>denied</c> stays
    /// reserved for ABAC refusals, and a compilation never produces one: a page the caller
    /// fails a gate for is absent from the candidate set, not refused (the RQL rule, §7),
    /// and the seam deliberately reports no per-page denial for a pruned listing. That
    /// includes a space-scoped read of a space the caller cannot enter — the same empty
    /// list, and the same <c>success</c> row, that <c>labels(spaceKey)</c> and
    /// <c>search(spaceKey:)</c> already write for it; the tree's <c>Denied</c> row for
    /// that case belongs to a directly requested subject, which a scope filter is not.</para>
    /// </summary>
    [AuditAction("graph.view")]
    [GraphQLType(typeof(NonNullType<PageGraphType>))]
    public async Task<CorePageGraph> PageGraph(
        string? spaceKey,
        [Service] IPageGraphService graphService,
        [Service] RocketWikiDbContext db,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return CorePageGraph.Empty;
        }

        var canonicalKey = SpaceKeys.CanonicalOrNull(spaceKey);
        Guid? spaceId = null;
        if (canonicalKey is not null)
        {
            // The Space query filter hides an archived space here, so its key reads as
            // unknown - exactly what the service would have answered for its id.
            spaceId = await db.Spaces
                .Where(s => s.Key == canonicalKey)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var graph = canonicalKey is not null && spaceId is null
            ? CorePageGraph.Empty
            : await graphService.GetGraphAsync(spaceId, principal, cancellationToken);

        await RecordGraphAuditAsync(auditSink, canonicalKey, spaceId, graph, cancellationToken);
        return graph;
    }

    /// <summary>The one <c>graph.view</c> row (see <see cref="PageGraph"/>'s doc). The
    /// details carry only the scope and two counts: bounded, operational, and nothing
    /// the caller was not just handed — never a node's title or marking (§15).</summary>
    private static Task RecordGraphAuditAsync(
        IAuditSink auditSink, string? canonicalKey, Guid? spaceId, CorePageGraph graph, CancellationToken ct)
    {
        var scope = canonicalKey ?? "instance";
        return auditSink.RecordAsync(
            new AuditRecord(
                "graph.view",
                AuditOutcome.Success,
                SubjectType: spaceId is null ? null : AuditSubjectType.Space,
                SubjectId: spaceId,
                SpaceKey: canonicalKey,
                DetailsJson: JsonSerializer.Serialize(new { scope, nodeCount = graph.Nodes.Count, edgeCount = graph.Edges.Count }),
                DedupKey: scope),
            ct);
    }
}
