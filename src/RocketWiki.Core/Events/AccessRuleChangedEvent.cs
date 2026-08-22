namespace RocketWiki.Core.Events;

/// <summary>
/// design.md §8: setSpaceGrants / setPageRestrictions mutations (create/update/delete
/// of one AccessRule). design.md §7: since AuditEvent is the only record of rule
/// history, Before/After must be complete rule-state snapshots, never a diff or
/// summary - Before is null for a creation, After is null for a deletion, and a replay
/// must be able to tell those apart from an ordinary update.
/// </summary>
public sealed record AccessRuleChangedEvent(
    Guid AccessRuleId,
    string? SpaceKey,
    Guid? ActorUserId,
    AccessRuleSnapshot? Before,
    AccessRuleSnapshot? After) : IDomainEvent;
