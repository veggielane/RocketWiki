namespace RocketWiki.Core.Events;

/// <summary>
/// design.md §8: "watch changes are audited as user actions" — so watch/unwatch flows
/// through the same domain-event pipeline as every other mutation. Neither event is
/// sync-relevant (data-model.md: Watch is instance-local, never synced), so
/// SyncOutboxWriter.Classify deliberately has no case for them — they feed audit only.
/// Exactly one of <see cref="PageId"/>/<see cref="SpaceId"/> is set, mirroring the
/// Watch row's own xor constraint.
/// </summary>
public sealed record WatchAddedEvent(Guid WatchId, Guid? PageId, Guid? SpaceId, string SpaceKey, Guid? ActorUserId) : IDomainEvent;

/// <summary>See <see cref="WatchAddedEvent"/> — same pipeline, same audit-only scope.</summary>
public sealed record WatchRemovedEvent(Guid WatchId, Guid? PageId, Guid? SpaceId, string SpaceKey, Guid? ActorUserId) : IDomainEvent;

/// <summary>
/// The recipient marked one of their own notifications read. Audit-only, like the watch
/// events (Notification is instance-local, never synced). Note the asymmetry design.md
/// §8 dictates: notification *delivery* is machine-generated and unaudited ("delivery is
/// not a content read"), but marking one read is a user action like any other mutation.
/// </summary>
public sealed record NotificationMarkedReadEvent(long NotificationId, Guid? ActorUserId) : IDomainEvent;
