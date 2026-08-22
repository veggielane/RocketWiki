namespace RocketWiki.Core.Events;

/// <summary>An existing comment's body was edited.</summary>
public sealed record CommentEditedEvent(Guid CommentId, Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId) : IDomainEvent;

/// <summary>Tombstone delete: body blanked, node retained so replies keep their parent (data-model.md).</summary>
public sealed record CommentDeletedEvent(Guid CommentId, Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId) : IDomainEvent;
