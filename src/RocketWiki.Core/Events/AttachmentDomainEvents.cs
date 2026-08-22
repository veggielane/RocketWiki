namespace RocketWiki.Core.Events;

/// <summary>design.md §10: bytes are written to storage before this event is raised - see IAttachmentService.</summary>
public sealed record AttachmentAddedEvent(Guid AttachmentId, Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, string FileName) : IDomainEvent;

/// <summary>Soft delete (data-model.md) - the blob is left in storage; a future purge job owns removing it, mirroring Page's 30-day trash pattern.</summary>
public sealed record AttachmentDeletedEvent(Guid AttachmentId, Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId) : IDomainEvent;
