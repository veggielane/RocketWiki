using HotChocolate.Types;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Adds <c>labels</c> to tree nodes so the space browser's label-filter facet has a
/// data source (web/src/labels/filterTreeByLabel.ts — design.md §8's known-deltas
/// list). A type extension rather than a change to Core's <see cref="PageTreeNode"/>:
/// the record is shared with the MCP <c>get_page_tree</c> tool, whose published
/// payload should not grow as a side effect of a GraphQL facet.
///
/// Leak analysis (design.md §6.7): every PageTreeNode was built by the pruned tree
/// walk — a node the caller can't view does not exist here by construction — and its
/// labels are the same space-level taxonomy <c>Query.labels</c> already grants to
/// anyone with a role in the space. Resolution batches through
/// <see cref="LabelRefsByPageIdDataLoader"/>: one PageLabels query for the visible
/// tree's page ids, joined in memory — never one query per node. No extra audit row:
/// the tree read is already audited as <c>space.browse</c> at the root field, and a
/// visible node's label names are part of that same browse, not a distinct action
/// (§7 audits user actions, not each field of one).
/// </summary>
public sealed class PageTreeNodeTypeExtension : ObjectTypeExtension<PageTreeNode>
{
    protected override void Configure(IObjectTypeDescriptor<PageTreeNode> descriptor)
    {
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
