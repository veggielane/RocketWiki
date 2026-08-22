namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §8's `tree: [PageTreeNode!]!` field. A pruned view, not the full Page
/// entity: a node that fails canView is dropped along with its entire Children subtree
/// before it ever gets here (design.md §6.7) - there is no way to construct a
/// PageTreeNode for a page the caller can't see, by construction.
/// </summary>
public sealed record PageTreeNode(Guid Id, string Title, string Slug, int SortOrder, IReadOnlyList<PageTreeNode> Children);
