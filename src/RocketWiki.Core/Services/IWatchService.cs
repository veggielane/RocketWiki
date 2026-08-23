using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §8 / data-model.md: Watch — explicit subscription to a single page (and
/// its subtree, via space/page watcher candidate lookup at fan-out) or a whole space.
///
/// Authorization: watching inherits the target's own read gate — page watch requires
/// canView on the page (you can't watch what you can't see), space watch requires any
/// space role, same as the space appearing in <c>Query.spaces</c>. **Unwatching checks
/// neither**: it deletes only the caller's own Watch row and must remain possible for
/// a page/space the caller has since lost access to — otherwise a lost grant would
/// pin a dead subscription forever. (The subscription is already inert either way:
/// fan-out re-evaluates canView per recipient at send time, design.md §8.)
///
/// Replica spaces are deliberately watchable: a Watch row is instance-local user
/// metadata (data-model.md: Watch/Notification "instance-local, never synced"), not a
/// write into the replica space's synced content, so design.md §12's read-only
/// invariant (which EffectivePermissionCalculator enforces on canEdit) does not apply
/// — and watching a replica is exactly how a user hears that a sync bundle changed it.
/// </summary>
public interface IWatchService
{
    /// <summary>Idempotent: watching an already-watched page returns the existing Watch (no event, nothing changed).</summary>
    Task<PageMutationResult<Watch>> WatchPageAsync(WatchPageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>NotFound when no Watch row exists for the caller and this page. No canView check — see interface doc.</summary>
    Task<PageMutationResult<Watch>> UnwatchPageAsync(UnwatchPageRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>Idempotent: watching an already-watched space returns the existing Watch (no event, nothing changed).</summary>
    Task<PageMutationResult<Watch>> WatchSpaceAsync(WatchSpaceRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>NotFound when no Watch row exists for the caller and this space. No role check — see interface doc.</summary>
    Task<PageMutationResult<Watch>> UnwatchSpaceAsync(UnwatchSpaceRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);
}

public sealed record WatchPageRequest(Guid PageId);

public sealed record UnwatchPageRequest(Guid PageId);

public sealed record WatchSpaceRequest(Guid SpaceId);

public sealed record UnwatchSpaceRequest(Guid SpaceId);
