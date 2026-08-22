namespace RocketWiki.Core.Events;

/// <summary>design.md §8: createPage mutation.</summary>
public sealed record PageCreatedEvent(Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, string Title) : IDomainEvent;

/// <summary>design.md §8: updatePage mutation - a new PageRevision was saved.</summary>
public sealed record PageContentUpdatedEvent(Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, int RevisionNumber) : IDomainEvent;

/// <summary>design.md §8: movePage mutation. AncestorPath rewrites happen alongside this, not represented here.</summary>
public sealed record PageMovedEvent(
    Guid PageId,
    Guid SpaceId,
    string SpaceKey,
    Guid? ActorUserId,
    Guid? OldParentPageId,
    Guid? NewParentPageId) : IDomainEvent;

/// <summary>design.md §8: deletePage mutation (soft delete).</summary>
public sealed record PageDeletedEvent(Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId) : IDomainEvent;

/// <summary>Restoring a soft-deleted page from trash.</summary>
public sealed record PageRestoredEvent(Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId) : IDomainEvent;

/// <summary>
/// design.md §6.4.1: cascading subtree delete, audited as one operation covering
/// PageIds.Count pages (root included). PageIds carries the full set - needed by the
/// sync outbox (§12), which must replicate concrete deletions, not a count - but
/// DomainEventAuditMapper deliberately only ever extracts the count for AuditEvent, per
/// §6.4.1's "reports how many, never which".
/// </summary>
public sealed record PageSubtreeDeletedEvent(Guid RootPageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, IReadOnlyList<Guid> PageIds) : IDomainEvent;

/// <summary>Reverses a PageSubtreeDeletedEvent, restoring exactly the pages deleted together with it. See PageSubtreeDeletedEvent for why PageIds (not just a count) is here.</summary>
public sealed record PageSubtreeRestoredEvent(Guid RootPageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, IReadOnlyList<Guid> PageIds) : IDomainEvent;

/// <summary>design.md §8: restoreRevision mutation.</summary>
public sealed record PageRevisionRestoredEvent(Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, int RestoredRevisionNumber) : IDomainEvent;

/// <summary>design.md §8: addComment mutation.</summary>
public sealed record CommentAddedEvent(Guid CommentId, Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId) : IDomainEvent;
