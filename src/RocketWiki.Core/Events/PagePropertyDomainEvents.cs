namespace RocketWiki.Core.Events;

/// <summary>
/// A page property was given a value (design.md §20). Carries the key's display NAME
/// alongside its id: the audit row and the sync payload both want the name (an auditor
/// recognizes "Owner", not a Guid; a replica has never seen this instance's registry
/// ids), while the id keeps the row joinable while it exists.
///
/// Both <c>SpaceId</c> and <c>SpaceKey</c> are present because the two consumers of this
/// pipeline need different ones: the audit mapper writes <c>SpaceKey</c> onto the row,
/// and <c>SyncOutboxWriter</c> looks the tracked Space up by key.
/// </summary>
public sealed record PagePropertySetEvent(
    Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, Guid PropertyKeyId, string Key, string Value) : IDomainEvent;

/// <summary>See <see cref="PagePropertySetEvent"/>. Carries no value — the row is gone,
/// and §7 records that the property was removed, not what it used to say (the previous
/// value is already in this page's earlier <c>page.property.set</c> row).</summary>
public sealed record PagePropertyRemovedEvent(
    Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, Guid PropertyKeyId, string Key) : IDomainEvent;

/// <summary>
/// An instance admin added a key to the page-property registry (design.md §20 / §7:
/// admin actions on shared vocabulary are user actions like any other). Flows through
/// the domain-event pipeline so the audit row commits in the same transaction as the
/// registry row.
///
/// Deliberately not sync-relevant — <c>SyncOutboxWriter.Classify</c> has no case for it
/// and there is no space to journal it against: the registry is instance-local, and a
/// replica materializes the keys it needs by name when a property event arrives.
/// </summary>
public sealed record PagePropertyKeyCreatedEvent(Guid PropertyKeyId, string Key, Guid ActorUserIdValue) : IDomainEvent
{
    public Guid? ActorUserId => ActorUserIdValue;
}

/// <summary>See <see cref="PagePropertyKeyCreatedEvent"/> — same pipeline, same
/// instance-local scope. Only ever raised for a key no page is using: deleting a key in
/// use is refused outright (design.md §20), so this event never means "values were
/// destroyed".</summary>
public sealed record PagePropertyKeyDeletedEvent(Guid PropertyKeyId, string Key, Guid ActorUserIdValue) : IDomainEvent
{
    public Guid? ActorUserId => ActorUserIdValue;
}
