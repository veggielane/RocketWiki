using HotChocolate.Types;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The document graph on the wire (design.md §6.7 / §21.8): every node a page the caller
/// may view, every edge a <c>page://</c> link between two such pages. Explicit types with
/// explicit field binding, like <see cref="PageTreeNodeType"/>, so inference never runs
/// over Core's records and the published field list is the hand-curated one the
/// introspection allowlist test pins.
///
/// <para><b>An OMITTING surface, and the types make that structural.</b> There is no
/// placeholder node type, no <c>denial</c> field and no union: a <see cref="PageGraphNode"/>
/// exists only because the graph service built one after the page passed <c>canView</c>,
/// and an edge exists only when both of its endpoints are nodes (<see cref="PageGraph.Induce"/>).
/// A page the caller cannot view therefore has no representation here at all — not as a
/// node, not as an edge endpoint, not as a count — which is the whole difference from the
/// tree's <c>ProtectedTreeNode</c> (a reader inside a space needs a name for the gap; a
/// compilation of the instance must not be a census of what they may not read).</para>
/// </summary>
public sealed class PageGraphType : ObjectType<PageGraph>
{
    protected override void Configure(IObjectTypeDescriptor<PageGraph> descriptor)
    {
        descriptor.Name("PageGraph");
        descriptor.BindFieldsExplicitly();

        descriptor.Field(g => g.Nodes)
            .Type<NonNullType<ListType<NonNullType<PageGraphNodeType>>>>();

        descriptor.Field(g => g.Edges)
            .Type<NonNullType<ListType<NonNullType<PageGraphEdgeType>>>>();
    }
}

/// <summary>
/// One node: what a screen needs to draw it and navigate to it — id, title, space key,
/// slug, icon and the marking. Every field is something the caller was already allowed
/// to read (the node exists only because they passed the gate for exactly this page), and
/// <c>marking</c> is the value the gate consulted, carried out of the same batch rather
/// than re-loaded — <see cref="PageGraphNode"/>'s doc gives the reason. The Core record's
/// <c>SpaceId</c> is deliberately not bound: the SPA addresses pages by key and slug, and
/// a hand-curated schema exposes what a client needs rather than what a record carries.
/// </summary>
public sealed class PageGraphNodeType : ObjectType<PageGraphNode>
{
    protected override void Configure(IObjectTypeDescriptor<PageGraphNode> descriptor)
    {
        descriptor.Name("PageGraphNode");
        descriptor.BindFieldsExplicitly();

        descriptor.Field(n => n.Id);
        descriptor.Field(n => n.Title);
        descriptor.Field(n => n.SpaceKey);
        descriptor.Field(n => n.Slug);
        descriptor.Field(n => n.Icon);
        descriptor.Field(n => n.Marking);
    }
}

/// <summary>
/// One directed edge, by page id on both ends plus the link's first-occurrence ordinal
/// in the source's content (<see cref="PageGraphEdge"/>). Both ids name nodes of the same
/// graph by construction, so an edge never identifies a page the caller cannot view.
/// </summary>
public sealed class PageGraphEdgeType : ObjectType<PageGraphEdge>
{
    protected override void Configure(IObjectTypeDescriptor<PageGraphEdge> descriptor)
    {
        descriptor.Name("PageGraphEdge");
        descriptor.BindFieldsExplicitly();

        descriptor.Field(e => e.SourcePageId);
        descriptor.Field(e => e.TargetPageId);
        descriptor.Field(e => e.Ordinal);
    }
}
