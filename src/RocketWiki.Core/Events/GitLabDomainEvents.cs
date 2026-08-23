namespace RocketWiki.Core.Events;

/// <summary>
/// The user stored (or replaced) their GitLab personal access token. Flows through
/// the domain-event pipeline like every mutation (design.md §7) so the audit row
/// commits in the same transaction as the credential row. Deliberately carries the
/// actor only — never the token, in any form: the audit row, the outbox (which
/// ignores these events — SyncOutboxWriter.Classify has no case, credentials are
/// instance-local), and telemetry must all be structurally unable to see token
/// material because it never enters the event.
/// </summary>
public sealed record GitLabTokenSetEvent(Guid ActorUserIdValue) : IDomainEvent
{
    public Guid? ActorUserId => ActorUserIdValue;
}

/// <summary>See <see cref="GitLabTokenSetEvent"/> — same pipeline, same no-token-material rule.</summary>
public sealed record GitLabTokenClearedEvent(Guid ActorUserIdValue) : IDomainEvent
{
    public Guid? ActorUserId => ActorUserIdValue;
}
