using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Core.Tests.Access;
using Xunit;

namespace RocketWiki.Core.Tests.Content;

/// <summary>
/// design.md §6.7: the document graph is an omitting surface, and an edge with one hidden
/// endpoint would disclose that the hidden page exists. <see cref="PageGraph.Induce"/> is
/// the one place the both-endpoints rule is written; these pin it in isolation from the
/// permission gate (which decides the node set and is tested in the Data tier).
/// </summary>
public class PageGraphTests
{
    private static readonly Guid Visible1 = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Visible2 = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Hidden = Guid.Parse("99999999-9999-4999-8999-999999999999");

    private static PageGraphNode Node(Guid id) =>
        new(id, Guid.Empty, "ENG", id.ToString("N"), id.ToString("N"), null,
            PageMarkingView.From(ProtectiveMarking.Baseline, TestCatalogs.Fruit));

    [Fact]
    public void Induce_KeepsOnlyEdgesWhoseBothEndpointsAreVisible()
    {
        var nodes = new[] { Node(Visible1), Node(Visible2) };
        var candidates = new[]
        {
            new PageGraphEdge(Visible1, Visible2, 0),
            new PageGraphEdge(Visible1, Hidden, 1),   // visible source, hidden target
            new PageGraphEdge(Hidden, Visible2, 0),   // hidden source, visible target
            new PageGraphEdge(Hidden, Hidden, 0),     // both hidden
            new PageGraphEdge(Visible2, Visible1, 0),
        };

        var graph = PageGraph.Induce(nodes, candidates);

        Assert.Equal(nodes, graph.Nodes);
        Assert.Equal(
            [new PageGraphEdge(Visible1, Visible2, 0), new PageGraphEdge(Visible2, Visible1, 0)],
            graph.Edges);
        Assert.DoesNotContain(graph.Edges, e => e.SourcePageId == Hidden || e.TargetPageId == Hidden);
    }

    [Fact]
    public void Induce_EveryEdgeEndpointIsANode_ByConstruction()
    {
        var nodes = new[] { Node(Visible1) };
        var candidates = new[]
        {
            new PageGraphEdge(Visible1, Visible1, 0), // a self-link survives: both ends are the node
            new PageGraphEdge(Visible1, Visible2, 1), // Visible2 is not in this node set
        };

        var graph = PageGraph.Induce(nodes, candidates);

        var nodeIds = graph.Nodes.Select(n => n.Id).ToHashSet();
        Assert.All(graph.Edges, e =>
        {
            Assert.Contains(e.SourcePageId, nodeIds);
            Assert.Contains(e.TargetPageId, nodeIds);
        });
        Assert.Equal([new PageGraphEdge(Visible1, Visible1, 0)], graph.Edges);
    }

    [Fact]
    public void Induce_OrdersEdgesBySourceThenOrdinal()
    {
        var nodes = new[] { Node(Visible1), Node(Visible2) };
        var candidates = new[]
        {
            new PageGraphEdge(Visible2, Visible1, 1),
            new PageGraphEdge(Visible1, Visible2, 1),
            new PageGraphEdge(Visible2, Visible2, 0),
            new PageGraphEdge(Visible1, Visible1, 0),
        };

        var graph = PageGraph.Induce(nodes, candidates);

        Assert.Equal(
            [
                new PageGraphEdge(Visible1, Visible1, 0),
                new PageGraphEdge(Visible1, Visible2, 1),
                new PageGraphEdge(Visible2, Visible2, 0),
                new PageGraphEdge(Visible2, Visible1, 1),
            ],
            graph.Edges);
    }

    [Fact]
    public void Induce_NoVisibleNodes_NoEdges()
    {
        var graph = PageGraph.Induce([], [new PageGraphEdge(Visible1, Visible2, 0)]);

        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void Neighbours_CountsAreTheListLengths()
    {
        var neighbours = new PageLinkNeighbours([Node(Visible1), Node(Visible2)], [Node(Visible2)]);

        Assert.Equal(2, neighbours.OutboundCount);
        Assert.Equal(1, neighbours.InboundCount);
    }
}
