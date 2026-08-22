using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §6.7: "every read path enforces canView" - page fetch, tree (filtered
/// during the walk, ancestor restrictions carried down the recursion), and revision
/// history. This is the shared read path GraphQL resolvers and MCP tools both call
/// (design.md §8); search and attachment streaming enforce canView through their own
/// paths and are not part of this interface.
///
/// The load-bearing rule for every method here: a page that fails canView must be
/// indistinguishable from a page that does not exist. Returning null for both (rather
/// than, say, a typed "Forbidden" result the way the mutation side does) is deliberate -
/// a caller-visible distinction between "not found" and "not allowed to see" is itself
/// a leak, since it confirms something exists at that id/location.
/// </summary>
public interface IPageReadService
{
    /// <summary>Null if the page does not exist OR the principal cannot view it - never distinguishable from outside this call.</summary>
    Task<Page?> GetPageAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// The space's page tree, restricted to nodes the principal can view. A node that
    /// fails canView is pruned along with its entire subtree (restrictions only ever
    /// accumulate going down, so a hidden ancestor implies every descendant is hidden
    /// too - design.md §6.4). Returns an empty list if the space doesn't exist, the
    /// principal has no space role at all, or every top-level page is restricted -
    /// these are deliberately not distinguished from each other either.
    /// </summary>
    Task<IReadOnlyList<PageTreeNode>> GetPageTreeAsync(Guid spaceId, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>Null under the exact same conditions as GetPageAsync for the same pageId - revision history requires nothing beyond canView on the page itself.</summary>
    Task<IReadOnlyList<PageRevision>?> GetRevisionHistoryAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default);
}
