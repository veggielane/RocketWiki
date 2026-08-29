namespace RocketWiki.Core.Events;

public sealed record SpaceCreatedEvent(Guid SpaceId, string Key, Guid? ActorUserId) : IDomainEvent;

public sealed record SpaceRenamedEvent(Guid SpaceId, string Key, Guid? ActorUserId, string OldName, string NewName) : IDomainEvent;

/// <summary>
/// The space's default page changed. <paramref name="NewPageId"/> is null when it was
/// cleared; both ids are carried so the audit row records what it was before, which a
/// single mutable column is otherwise the only history of.
///
/// <para>Like every other space event, this never becomes a sync event
/// (SyncOutboxWriter.Classify has no arm for it): design.md §12's table puts space
/// lifecycle and identity in the "stays local" column, so each side chooses its own
/// default page — which is also the only coherent answer for a value that is a page
/// reference, since the high side may not hold the page the low side chose.</para>
/// </summary>
public sealed record SpaceHomepageSetEvent(
    Guid SpaceId, string Key, Guid? ActorUserId, Guid? OldPageId, Guid? NewPageId) : IDomainEvent;

/// <summary>Soft delete - Space uses the same IsDeleted/DeletedAtUtc/DeletedByUserId pattern as Page (data-model.md).</summary>
public sealed record SpaceArchivedEvent(Guid SpaceId, string Key, Guid? ActorUserId) : IDomainEvent;

public sealed record SpaceRestoredEvent(Guid SpaceId, string Key, Guid? ActorUserId) : IDomainEvent;
