namespace RocketWiki.Core.Events;

/// <summary>
/// An entry was created on a page (docs/ENTRIES-AND-FORMS-PLAN.md).
///
/// <para>Carries the collection but deliberately <b>not the entry's data or its
/// marking level</b>. The data is user content of unbounded shape and would put a form
/// submission's contents into the audit row, where §7's retention and access rules are
/// not the ones the entry's own marking implies. The level is omitted for the reason
/// §21.8 keeps it out of telemetry dimensions: an audit trail that carried it would let
/// anyone entitled to read audit rows count the classified estate without ever reading
/// an entry. Both remain readable through the entry itself, by someone cleared for it.</para>
///
/// <para>Both <c>SpaceId</c> and <c>SpaceKey</c> are present because the two consumers of
/// this pipeline need different ones: the audit mapper writes <c>SpaceKey</c> onto the
/// row, and <c>SyncOutboxWriter</c> looks the tracked Space up by key.</para>
/// </summary>
public sealed record PageEntryCreatedEvent(
    Guid EntryId, Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, string Collection) : IDomainEvent;

/// <summary>See <see cref="PageEntryCreatedEvent"/>. A marking change is not a separate
/// event: an entry's marking rides on its update, because unlike a page there is no path
/// that changes one without the other.</summary>
public sealed record PageEntryUpdatedEvent(
    Guid EntryId, Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, string Collection) : IDomainEvent;

/// <summary>See <see cref="PageEntryCreatedEvent"/>. A soft delete, so the row survives
/// for sync and restore.</summary>
public sealed record PageEntryDeletedEvent(
    Guid EntryId, Guid PageId, Guid SpaceId, string SpaceKey, Guid? ActorUserId, string Collection) : IDomainEvent;
