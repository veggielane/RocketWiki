namespace RocketWiki.Core.Events;

public sealed record LabelCreatedEvent(Guid LabelId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, string Name) : IDomainEvent;

public sealed record LabelAttachedEvent(Guid LabelId, Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, string LabelName) : IDomainEvent;

public sealed record LabelDetachedEvent(Guid LabelId, Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, string LabelName) : IDomainEvent;
