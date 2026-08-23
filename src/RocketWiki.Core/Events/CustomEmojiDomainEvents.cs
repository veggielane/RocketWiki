namespace RocketWiki.Core.Events;

/// <summary>
/// An instance admin added a named emoji to the registry (design.md §7: admin actions
/// on shared vocabulary are user actions like any other). Flows through the
/// domain-event pipeline so the audit row commits in the same transaction as the
/// registry row. Deliberately not sync-relevant: emoji definitions are instance-local
/// in v1 (SyncOutboxWriter.Classify has no case — content carrying <c>:name:</c>
/// travels as plain text and degrades to literal text where the name is unknown).
/// </summary>
public sealed record CustomEmojiCreatedEvent(Guid EmojiId, string Name, Guid ActorUserIdValue) : IDomainEvent
{
    public Guid? ActorUserId => ActorUserIdValue;
}

/// <summary>
/// See <see cref="CustomEmojiCreatedEvent"/> — same pipeline, same instance-local
/// scope. Deleting a definition leaves every <c>:name:</c> occurrence in content
/// untouched; it simply renders as literal text again, harmless by construction.
/// </summary>
public sealed record CustomEmojiDeletedEvent(Guid EmojiId, string Name, Guid ActorUserIdValue) : IDomainEvent
{
    public Guid? ActorUserId => ActorUserIdValue;
}
