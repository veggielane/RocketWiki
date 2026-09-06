using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Services;

/// <summary>
/// A node of the document graph: a page the caller CAN view. Like <see cref="PageTreeNode"/>,
/// there is no way to build one for a page the caller cannot see — the graph service only
/// constructs a node after the page passed canView, so every field here is something the
/// caller was already allowed to read. Deliberately not the <see cref="Entities.Page"/>
/// entity: a whole-instance graph resolves thousands of pages, and an entity per node
/// would drag every page's Markdown through memory to draw a dot; the API resolves a full
/// <c>Page</c> for a node it wants more of through the ordinary gated loader.
///
/// <para><see cref="Marking"/> is the same value the gate passed on, carried out rather
/// than re-loaded, for the reason <see cref="PageTreeNode"/> gives: a graph that badged a
/// node with a marking other than the one it enforced would be the wrong kind of wrong.
/// Leak-safe by the same construction — the node exists only because the caller passed
/// the gate for that marking.</para>
/// </summary>
public sealed record PageGraphNode(
    Guid Id,
    Guid SpaceId,
    string SpaceKey,
    string Slug,
    string Title,
    /// <summary>Decoration only (design.md §4); nothing gates on it.</summary>
    PageIcon? Icon,
    PageMarkingView Marking);

/// <summary>
/// A directed edge: <see cref="SourcePageId"/>'s content links to <see cref="TargetPageId"/>.
/// <see cref="Ordinal"/> is the link's first-occurrence position among the source's
/// distinct targets (0-based), so a client holding the source's content can pair the edge
/// with its anchor. A page that links to itself is an edge from a node to itself; the index
/// records what the content says and the read path does not editorialize.
/// </summary>
public sealed record PageGraphEdge(Guid SourcePageId, Guid TargetPageId, int Ordinal);

/// <summary>
/// The graph the caller may see. Invariant, pinned by <see cref="Induce"/> and by test:
/// every edge's source AND target is in <see cref="Nodes"/>.
/// </summary>
public sealed record PageGraph(IReadOnlyList<PageGraphNode> Nodes, IReadOnlyList<PageGraphEdge> Edges)
{
    public static readonly PageGraph Empty = new([], []);

    /// <summary>
    /// The subgraph induced on <paramref name="visibleNodes"/>: those nodes, and only the
    /// candidate edges whose BOTH endpoints are among them (design.md §6.7 — an edge to
    /// an omitted page would disclose that the page exists, which is exactly what
    /// omission is for). Pure; the caller has already decided which nodes are visible
    /// through the permission gate, and this is the one place the both-endpoints rule is
    /// written, so a second graph surface cannot get it half right. Edges are returned in
    /// source-then-ordinal order; nodes in the order given.
    /// </summary>
    public static PageGraph Induce(IReadOnlyList<PageGraphNode> visibleNodes, IEnumerable<PageGraphEdge> candidateEdges)
    {
        var visibleIds = new HashSet<Guid>(visibleNodes.Count);
        foreach (var node in visibleNodes)
        {
            visibleIds.Add(node.Id);
        }

        var edges = candidateEdges
            .Where(e => visibleIds.Contains(e.SourcePageId) && visibleIds.Contains(e.TargetPageId))
            .OrderBy(e => e.SourcePageId)
            .ThenBy(e => e.Ordinal)
            .ToList();

        return new PageGraph(visibleNodes, edges);
    }
}

/// <summary>
/// One page's neighbours in the graph (see <see cref="IPageGraphService.GetPageLinksAsync"/>):
/// <see cref="Outbound"/> in the page's own first-occurrence order, <see cref="Inbound"/>
/// by title then id. Both hold only pages the caller can view, and the counts are the
/// list lengths — computed here, once, so no API projection can count the unfiltered
/// set by mistake. A properties panel that shows a count and expands to the list is
/// reading one value twice.
/// </summary>
public sealed record PageLinkNeighbours(IReadOnlyList<PageGraphNode> Outbound, IReadOnlyList<PageGraphNode> Inbound)
{
    public int OutboundCount => Outbound.Count;

    public int InboundCount => Inbound.Count;
}
