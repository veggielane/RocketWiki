namespace RocketWiki.Core.Events;

/// <summary>
/// The user set (or replaced) their own profile picture. Flows through the
/// domain-event pipeline like every mutation (design.md §7) so the audit row
/// commits in the same transaction as the <c>UserAvatar</c> row. Carries the actor
/// only — never image bytes, the storage key, or the email hashes: the audit row,
/// the sync outbox (which ignores these events — <c>SyncOutboxWriter.Classify</c>
/// has no case, avatars are instance-local), and telemetry are all structurally
/// unable to see any of them because none ever enters the event. Same shape and
/// reasoning as <see cref="GitLabTokenSetEvent"/>.
/// </summary>
public sealed record AvatarSetEvent(Guid ActorUserIdValue) : IDomainEvent
{
    public Guid? ActorUserId => ActorUserIdValue;
}

/// <summary>See <see cref="AvatarSetEvent"/> — same pipeline, same nothing-but-the-actor rule.</summary>
public sealed record AvatarClearedEvent(Guid ActorUserIdValue) : IDomainEvent
{
    public Guid? ActorUserId => ActorUserIdValue;
}
