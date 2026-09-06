using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data.Access;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed <see cref="IPageGraphService"/> (the contract, and the leak posture, are on
/// the interface). Lives in RocketWiki.Data for the reason every read service does: it
/// needs the DbContext and the internal <see cref="PermissionContextLoader"/>.
///
/// <para><b>Constant queries, one gate.</b> Both reads follow the shape of
/// <c>PageReadService.GetPagesAsync</c> and <c>SearchService</c>'s post-filter: project
/// the candidate pages straight from SQL (never the entity — a whole-instance graph would
/// otherwise pull every page's Markdown to draw a dot), hand every candidate to the
/// loader's batch as a <see cref="PermissionSubject"/> (three queries for any batch size),
/// and decide each page with <see cref="PagePermissionContext.Compute"/> in memory. There
/// is no rule evaluation in this file; <see cref="Visible"/> is the single place a
/// candidate becomes a node, and it does so only on the calculator's verdict.</para>
///
/// <para><b>What "live" means here.</b> A candidate is a page that survives the Page and
/// Space query filters — not trashed, and not in an archived space (the required
/// <c>Space</c> navigation applies the space's filter, exactly as search's candidate
/// projection does). The index keeps rows for trashed pages and dangling targets; this is
/// where they drop out, on both ends of every edge, because an edge is built only from
/// nodes and a node is built only from a live, viewable candidate.</para>
/// </summary>
public sealed class PageGraphService : IPageGraphService
{
    private readonly RocketWikiDbContext _db;
    private readonly PermissionContextLoader _permissions;

    public PageGraphService(RocketWikiDbContext db)
    {
        _db = db;
        _permissions = new PermissionContextLoader(db, noTracking: true);
    }

    /// <inheritdoc />
    public async Task<PageGraph> GetGraphAsync(Guid? spaceId, Principal principal, CancellationToken cancellationToken = default)
    {
        var candidates = LivePages();
        if (spaceId is { } scope)
        {
            candidates = candidates.Where(p => p.SpaceId == scope);
        }

        var rows = await Project(candidates).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return PageGraph.Empty;
        }

        var visible = Visible(rows, await LoadBatchAsync(rows, cancellationToken), principal);
        if (visible.Count == 0)
        {
            return PageGraph.Empty;
        }

        // Every index row in scope, tiny and untracked; the both-endpoints rule is applied
        // in memory by PageGraph.Induce against the nodes that passed the gate above. For
        // a space scope the rows are narrowed by SOURCE in SQL — a link whose source is
        // outside the space can never be an edge of the space's subgraph — and a link
        // whose target lies outside is dropped by Induce like any edge to a non-node.
        var links = _db.PageLinks.AsNoTracking();
        if (spaceId is { } sourceScope)
        {
            links = links.Where(l => _db.Pages.Any(p => p.Id == l.SourcePageId && p.SpaceId == sourceScope));
        }

        var edges = await links
            .Select(l => new PageGraphEdge(l.SourcePageId, l.TargetPageId, l.Ordinal))
            .ToListAsync(cancellationToken);

        var nodes = visible.Values
            .OrderBy(n => n.SpaceKey, StringComparer.Ordinal)
            .ThenBy(n => n.Title, StringComparer.Ordinal)
            .ThenBy(n => n.Id)
            .ToList();
        return PageGraph.Induce(nodes, edges);
    }

    /// <inheritdoc />
    public async Task<ReadResult<PageLinkNeighbours>> GetPageLinksAsync(
        Guid pageId, Principal principal, CancellationToken cancellationToken = default)
    {
        // One query for both directions; the split is in memory.
        var links = await _db.PageLinks.AsNoTracking()
            .Where(l => l.SourcePageId == pageId || l.TargetPageId == pageId)
            .ToListAsync(cancellationToken);
        var outboundIds = links.Where(l => l.SourcePageId == pageId).OrderBy(l => l.Ordinal).Select(l => l.TargetPageId).ToList();
        var inboundIds = links.Where(l => l.TargetPageId == pageId).Select(l => l.SourcePageId).ToList();

        // The page itself rides in the same batch as its neighbours: its own verdict
        // decides Found/Denied, and costs no extra round trip. A missing or dangling id
        // simply has no candidate row, so it can never become a node.
        var candidateIds = outboundIds.Concat(inboundIds).Append(pageId).Distinct().ToList();
        var rows = await Project(LivePages().Where(p => candidateIds.Contains(p.Id))).ToListAsync(cancellationToken);
        var self = rows.FirstOrDefault(r => r.Id == pageId);
        if (self is null)
        {
            return new ReadResult<PageLinkNeighbours>.NotFound();
        }

        var batch = await LoadBatchAsync(rows, cancellationToken);
        var permission = batch.For(Subject(self), isReplicaSpace: false).Compute(principal);
        if (!permission.CanView)
        {
            return new ReadResult<PageLinkNeighbours>.Denied(
                permission.ViewDenialReason ?? EffectivePermissionCalculator.NoSpaceAccessReason);
        }

        var visible = Visible(rows, batch, principal);

        // Both lists are drawn from the visible set and nothing else. A target the caller
        // cannot view is not an outbound entry; a source they cannot view is not an inbound
        // one — and because the counts are the lists' lengths, a hidden backlink does not
        // register as "one more page links here" either (design.md §6.7: no count that
        // implies something was removed).
        var outbound = outboundIds.Where(visible.ContainsKey).Select(id => visible[id]).ToList();
        var inbound = inboundIds.Where(visible.ContainsKey).Select(id => visible[id])
            .OrderBy(n => n.Title, StringComparer.Ordinal)
            .ThenBy(n => n.Id)
            .ToList();
        return new ReadResult<PageLinkNeighbours>.Found(new PageLinkNeighbours(outbound, inbound));
    }

    /// <summary>
    /// The nodes the principal may see, keyed by page id: each candidate decided by the
    /// calculator against its own chain and its own marking from the batch, and turned into
    /// a node only on a passing verdict. THE access filter for this surface — there is no
    /// other place a candidate row becomes a <see cref="PageGraphNode"/>. Replica status is
    /// irrelevant to canView (design.md §6.4), so false, as on every other read path.
    /// </summary>
    private Dictionary<Guid, PageGraphNode> Visible(
        IReadOnlyList<CandidateRow> rows, PermissionContextBatch batch, Principal principal)
    {
        var visible = new Dictionary<Guid, PageGraphNode>(rows.Count);
        foreach (var row in rows)
        {
            if (!batch.For(Subject(row), isReplicaSpace: false).Compute(principal).CanView)
            {
                continue;
            }

            visible[row.Id] = ToNode(row, batch);
        }

        return visible;
    }

    /// <summary>The marking carried out is the one the gate consulted, from the same batch
    /// (see <see cref="PageGraphNode"/>); never re-read.</summary>
    private PageGraphNode ToNode(CandidateRow row, PermissionContextBatch batch) =>
        new(row.Id, row.SpaceId, row.SpaceKey, row.Slug, row.Title, row.Icon,
            PageMarkingView.From(batch.MarkingFor(row.Id), _db.SelectorCatalog));

    private Task<PermissionContextBatch> LoadBatchAsync(IReadOnlyList<CandidateRow> rows, CancellationToken cancellationToken) =>
        _permissions.LoadBatchAsync(rows.Select(Subject).ToList(), cancellationToken);

    private static PermissionSubject Subject(CandidateRow row) => new(row.Id, row.SpaceId, row.AncestorPath);

    /// <summary>Live pages: the Page query filter hides the trash; the space filter is applied
    /// by <see cref="Project"/>. Filters compose on this BEFORE projection - EF cannot translate
    /// a predicate over a constructed record.</summary>
    private IQueryable<Page> LivePages() => _db.Pages.AsNoTracking();

    /// <summary>
    /// What a node and its permission subject need, and nothing else. <c>p.Space!.Key</c> is
    /// an inner join through the required navigation, so the Space query filter excludes an
    /// archived space's pages here exactly as it does for search candidates and slug lookups.
    /// </summary>
    private static IQueryable<CandidateRow> Project(IQueryable<Page> pages) =>
        pages.Select(p => new CandidateRow(p.Id, p.SpaceId, p.Space!.Key, p.Slug, p.Title, p.Icon, p.AncestorPath));

    private sealed record CandidateRow(
        Guid Id, Guid SpaceId, string SpaceKey, string Slug, string Title, PageIcon? Icon, string AncestorPath);
}
