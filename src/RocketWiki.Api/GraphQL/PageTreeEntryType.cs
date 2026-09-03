using HotChocolate.Types;
using RocketWiki.Core.Access;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// One entry of the page tree (design.md §6.7 / §21.8): a <see cref="PageTreeNodeType"/>
/// the caller may view, or a <see cref="ProtectedTreeNodeType"/> placeholder for one they
/// may not. A union rather than a flag on the node, deliberately: a flag would force
/// <c>id: UUID!</c> to carry a sentinel and <c>marking</c> to become nullable for every
/// node, and it would leave a client one forgotten null-check away from rendering a
/// placeholder's "id". The union makes it structurally impossible for a placeholder to
/// carry an id, a title, labels or children — the fields do not exist on its type.
/// </summary>
public sealed class PageTreeEntryType : UnionType<PageTreeEntry>
{
    protected override void Configure(IUnionTypeDescriptor descriptor)
    {
        descriptor.Name("PageTreeEntry");
        descriptor.Type<PageTreeNodeType>();
        descriptor.Type<ProtectedTreeNodeType>();
    }
}

/// <summary>
/// A page the caller may not view, at its position in the tree (design.md §6.7 / §21.8).
/// Three fields and no more: the placeholder title (the server constant every disclosing
/// surface renders), the sibling position, and the denial — the marking and every gate
/// the caller failed. <b>No id, slug, icon, labels or children</b>, and nothing beneath it
/// is ever walked: a placeholder is a leaf. <c>hasChildren</c> and <c>labels</c> live
/// only on the visible node type, so no DataLoader runs for a placeholder and no lookup
/// by a sentinel id ever happens.
/// </summary>
public sealed class ProtectedTreeNodeType : ObjectType<ProtectedTreeNode>
{
    protected override void Configure(IObjectTypeDescriptor<ProtectedTreeNode> descriptor)
    {
        descriptor.Name("ProtectedTreeNode");
        descriptor.BindFieldsExplicitly();

        descriptor.Field("title")
            .Type<NonNullType<StringType>>()
            .Resolve(_ => AccessDenialView.ProtectedTitle);

        descriptor.Field(n => n.SortOrder);

        // A tree placeholder has no page id to hand the projection - and needs none:
        // every failing restriction on it is its own (see GateResultView.Inherited).
        descriptor.Field("denial")
            .Type<NonNullType<ObjectType<AccessDenialView>>>()
            .Resolve(context => AccessDenialView.From(
                context.Parent<ProtectedTreeNode>().Denial, context.Service<SelectorCatalog>(), subjectPageId: null));
    }
}
