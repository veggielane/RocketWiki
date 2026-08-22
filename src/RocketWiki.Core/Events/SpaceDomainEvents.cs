namespace RocketWiki.Core.Events;

public sealed record SpaceCreatedEvent(Guid SpaceId, string Key, Guid? ActorUserId) : IDomainEvent;

public sealed record SpaceRenamedEvent(Guid SpaceId, string Key, Guid? ActorUserId, string OldName, string NewName) : IDomainEvent;

/// <summary>Soft delete - Space uses the same IsDeleted/DeletedAtUtc/DeletedByUserId pattern as Page (data-model.md).</summary>
public sealed record SpaceArchivedEvent(Guid SpaceId, string Key, Guid? ActorUserId) : IDomainEvent;

public sealed record SpaceRestoredEvent(Guid SpaceId, string Key, Guid? ActorUserId) : IDomainEvent;
