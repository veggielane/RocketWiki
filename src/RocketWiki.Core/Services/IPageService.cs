using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §8: the single shared path GraphQL resolvers and MCP tools both call for
/// page mutations - "no side-door writes" (senior-software-engineer.md). Implementations
/// must enforce canEdit via EffectivePermissionCalculator, refuse writes to replica
/// spaces, honour optimistic concurrency, and raise the matching domain event so the
/// mutation is audited in the same transaction (design.md §7).
///
/// <paramref name="principal"/> is used only for ABAC evaluation (groups/attributes/sub
/// from the token). <c>actingUserId</c> is the caller's already-resolved local User.Id -
/// JIT-provisioning a User row from the token's `sub` claim (design.md §11) is transport
/// middleware's job, not this service's, so it is not repeated here.
/// </summary>
public interface IPageService
{
    Task<PageMutationResult<Page>> CreatePageAsync(
        CreatePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="sessionContributorUserIds"/> (design.md §8 co-editing): the
    /// distinct users whose live edit-session updates fed this save, recorded as
    /// PageRevisionContributor rows in the same transaction as the revision. This is
    /// a SERVER-side parameter only — it must come from the API's edit-session
    /// registry (membership canEdit-gated at join), never from any client-supplied
    /// list, or attribution would be forgeable (§7). It is deliberately not part of
    /// <see cref="UpdatePageContentRequest"/>, because that record doubles as the
    /// GraphQL input type and a field there would be exactly such a client channel.
    /// Null/empty means an ordinary solo save: no contributor rows.
    /// </summary>
    Task<PageMutationResult<Page>> UpdatePageContentAsync(
        UpdatePageContentRequest request, Principal principal, Guid actingUserId, AuditContext auditContext,
        IReadOnlyCollection<Guid>? sessionContributorUserIds = null, CancellationToken cancellationToken = default);

    Task<PageMutationResult<Page>> MovePageAsync(
        MovePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// design.md §6.4.1: cascades to the whole live subtree as one audited operation,
    /// and requires canEdit on every page in it - if any page fails, the entire
    /// operation is refused (SubtreeOperationForbiddenError), never partially applied.
    /// </summary>
    Task<PageMutationResult<PageDeleteSummary>> DeletePageAsync(
        DeletePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>Reverses a DeletePageAsync operation: restores exactly the pages soft-deleted together with the target, subject to the same per-page canEdit check.</summary>
    Task<PageMutationResult<PageRestoreSummary>> RestorePageAsync(
        RestorePageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<Page>> RestoreRevisionAsync(
        RestoreRevisionRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);
}
