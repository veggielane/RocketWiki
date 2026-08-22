namespace RocketWiki.Core.Events;

public sealed record SyncImportedSpaceRange(string SpaceKey, long FromSequence, long ToSequence, int EventCount);

/// <summary>design.md §12: "every import is audited (sync.import with bundle id and event range)". ActorUserId is always null - import is a system action, not a user's.</summary>
public sealed record SyncImportedEvent(
    string OriginInstanceId, int BundleNumber, IReadOnlyList<SyncImportedSpaceRange> SpaceRanges, bool WasDuplicate) : IDomainEvent
{
    public Guid? ActorUserId => null;
}
