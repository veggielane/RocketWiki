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

/// <summary>
/// design.md §12: the space was flagged (or unflagged) <b>exported</b> — the low-side
/// switch that starts content flowing across the boundary into another security domain.
///
/// <para>Two audit actions from one event, the same shape <c>PageMarkingSetEvent</c> uses
/// for set-vs-downgrade and for the same reason: enabling export is the operationally
/// risky direction, and its own action name is what lets a reviewer find every space
/// somebody opened for export with one query. Disabling is the safe direction and gets
/// the quieter name.</para>
///
/// <para>Like every other space event this is deliberately NOT a sync event
/// (SyncOutboxWriter.Classify has no arm for it) — §12's table puts space lifecycle in
/// the "stays local" column, and exported-ness is a property of the LOW side's decision
/// about its own space, meaningless on the receiving instance (which always writes
/// <c>IsExported = false</c> for a replica).</para>
/// </summary>
public sealed record SpaceExportChangedEvent(
    Guid SpaceId, string Key, Guid? ActorUserId, bool Exported) : IDomainEvent;

/// <summary>
/// The space's designated owner changed — accountability metadata (design.md §6.5), never
/// access. Both ids travel for the same reason <see cref="SpaceHomepageSetEvent"/> carries
/// both: owner is a single mutable column, so the audit row is the only record of who was
/// responsible before the reassignment.
///
/// <para>User <b>ids</b> only, never display names: a name is mirrored, mutable, and
/// re-derivable from the id, and §7's record should pin the identity rather than a label
/// that may since have changed.</para>
///
/// <para>Like every other space event this is deliberately NOT a sync event
/// (SyncOutboxWriter.Classify has no arm for it): §12's table puts space lifecycle and
/// identity in the "stays local" column, and a user id is doubly meaningless across the
/// boundary since each side runs its own Keycloak.</para>
/// </summary>
public sealed record SpaceOwnerChangedEvent(
    Guid SpaceId, string Key, Guid? ActorUserId, Guid OldOwnerUserId, Guid NewOwnerUserId) : IDomainEvent;

/// <summary>Soft delete - Space uses the same IsDeleted/DeletedAtUtc/DeletedByUserId pattern as Page (data-model.md).</summary>
public sealed record SpaceArchivedEvent(Guid SpaceId, string Key, Guid? ActorUserId) : IDomainEvent;

public sealed record SpaceRestoredEvent(Guid SpaceId, string Key, Guid? ActorUserId) : IDomainEvent;
