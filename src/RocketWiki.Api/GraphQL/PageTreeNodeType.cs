using HotChocolate.Types;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Shapes Core's <see cref="PageTreeNode"/> for the GraphQL tree, and adds <c>labels</c>
/// so the space browser's label-filter facet has a data source
/// (web/src/labels/filterTreeByLabel.ts — design.md §8's known-deltas list). An explicit
/// object type with explicit field binding rather than convention-based inference,
/// because Core's record carries a <c>Children</c> list of <see cref="PageTreeEntry"/>
/// — visible nodes and protected placeholders alike (design.md §6.7/§21.8) — which is
/// published as the <see cref="PageTreeEntryType"/> union, not inferred. Core's record
/// itself stays unchanged and shared with the MCP <c>get_page_tree</c> tool, which maps
/// the visible nodes only (§21.8: MCP omits).
///
/// <para><b>Children are every entry, placeholders included.</b> A child the caller
/// cannot view is a <c>ProtectedTreeNode</c> at its sibling position, and
/// <c>hasChildren</c> answers "would <c>children</c> be non-empty" — so a node whose
/// only children are protected still draws a disclosure control, and expanding it shows
/// the placeholders rather than nothing. Counting visible children only would make the
/// two fields disagree in one response: no expander, yet placeholders beneath.</para>
///
/// Leak analysis (design.md §6.7): every PageTreeNode was built by the walk for a page
/// the caller passed every view gate on — a node the caller can't view is a different
/// type by construction — and its labels are the same space-level taxonomy
/// <c>Query.labels</c> already grants to anyone with access to the space. Resolution
/// batches through <see cref="LabelRefsByPageIdDataLoader"/>: one PageLabels query for
/// the visible tree's page ids, joined in memory — never one query per node. No extra
/// audit row: the tree read is already audited as <c>space.browse</c> at the root field,
/// and a visible node's label names are part of that same browse, not a distinct action
/// (§7 audits user actions, not each field of one).
/// </summary>
public sealed class PageTreeNodeType : ObjectType<PageTreeNode>
{
    protected override void Configure(IObjectTypeDescriptor<PageTreeNode> descriptor)
    {
        descriptor.Name("PageTreeNode");
        descriptor.BindFieldsExplicitly();

        descriptor.Field(n => n.Id);
        descriptor.Field(n => n.Title);
        descriptor.Field(n => n.Icon);
        descriptor.Field(n => n.Slug);
        descriptor.Field(n => n.SortOrder);
        descriptor.Field(n => n.HasRestrictions);
        descriptor.Field(n => n.OwnViewRestrictions);
        descriptor.Field(n => n.Marking);

        // Every child entry, visible or protected, in tree order - see the class doc.
        descriptor.Field("children")
            .Type<NonNullType<ListType<NonNullType<PageTreeEntryType>>>>()
            .Resolve(context => context.Parent<PageTreeNode>().Children);

        // Whether this node has children at all, independent of how many levels the
        // caller's query selected.
        //
        // The walk builds the whole tree in memory, so this is free here — but the
        // GraphQL DOCUMENT truncates it, and without this field the deepest selected
        // level is indistinguishable from a leaf. A tree that draws a disclosure
        // control only where children arrived was therefore asserting "this page has
        // none" about pages that have several. Resolved from the built node rather
        // than a query, so it cannot disagree with the children field: it is true
        // exactly when `children` would be non-empty, placeholders counted.
        descriptor.Field("hasChildren")
            .Type<NonNullType<BooleanType>>()
            .Resolve(context => context.Parent<PageTreeNode>().Children.Count > 0);

        descriptor.Field("labels")
            .Type<NonNullType<ListType<NonNullType<StringType>>>>()
            .Resolve(async context =>
            {
                var node = context.Parent<PageTreeNode>();
                var loader = context.DataLoader<LabelRefsByPageIdDataLoader>();
                var labels = await loader.LoadAsync(node.Id, context.RequestAborted);
                return (labels ?? []).Select(l => l.Name).ToList();
            });
    }
}
