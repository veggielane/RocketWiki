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
/// indistinguishable from a page that does not exist *to the caller of the API* - but
/// per design.md §6.7 ("indistinguishable to the caller, not to the audit log") the
/// distinction must survive internally so §7 can record the denial with its failing
/// restriction. Hence <see cref="ReadResult{T}"/>: NotFound and Denied are separate
/// cases here, the API layer audits Denied, and both collapse to the identical
/// null/empty response at the boundary. A service that returned bare null for both
/// would make denial auditing impossible - §6.7 names that as the anti-pattern.
/// </summary>
public interface IPageReadService
{
    /// <summary>
    /// Found(page) if the principal can view it; NotFound if no such live page exists;
    /// Denied(reason) if it exists but canView fails - where reason is the failing
    /// restriction (<c>restriction:{pageId}:{ruleId}</c>) or <c>no-space-role</c>,
    /// exactly as EffectivePermissionCalculator computed it.
    /// </summary>
    Task<ReadResult<Page>> GetPageAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// The space's page tree, restricted to nodes the principal can view. A node that
    /// fails canView is pruned along with its entire subtree (restrictions only ever
    /// accumulate going down, so a hidden ancestor implies every descendant is hidden
    /// too - design.md §6.4). Pruning is not a Denied result: the browse itself was
    /// permitted and simply shows less, the same as it does for everyone (§6.7 - no
    /// gaps that imply something was removed), so per-node denials are not reported
    /// or audited here. Denied("no-space-role") is returned only when the request as
    /// a whole is refused because the principal holds no role in the space at all;
    /// NotFound when the space doesn't exist. The API boundary collapses both to the
    /// same empty list Found can also legitimately carry.
    /// </summary>
    Task<ReadResult<IReadOnlyList<PageTreeNode>>> GetPageTreeAsync(Guid spaceId, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>Found/NotFound/Denied under the exact same conditions as GetPageAsync for the same pageId - revision history requires nothing beyond canView on the page itself.</summary>
    Task<ReadResult<IReadOnlyList<PageRevision>>> GetRevisionHistoryAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default);
}
