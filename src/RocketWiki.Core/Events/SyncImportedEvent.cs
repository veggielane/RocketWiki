namespace RocketWiki.Core.Events;

public sealed record SyncImportedSpaceRange(string SpaceKey, long FromSequence, long ToSequence, int EventCount);

/// <summary>design.md §12: "every import is audited (sync.import with bundle id and event range)". ActorUserId is always null - import is a system action, not a user's.</summary>
public sealed record SyncImportedEvent(
    string OriginInstanceId, int BundleNumber, IReadOnlyList<SyncImportedSpaceRange> SpaceRanges, bool WasDuplicate) : IDomainEvent
{
    public Guid? ActorUserId => null;
}

/// <summary>
/// A bundle was refused for integrity — gap, chain break, payload-hash mismatch,
/// per-space sequence gap, or an unreadable/structurally broken file (design.md §12:
/// "a detected error, never a silent absorb"). Raised so the refusal leaves a durable
/// audit row instead of only a CLI exit code; nothing about the refused bundle is
/// applied, so this event must ONLY ever be raised on a fresh unit of work, never the
/// one that holds the partially-applied bundle. <see cref="Reason"/> is a stable
/// machine-readable kind (e.g. <c>bundle_gap</c>); <see cref="Detail"/> is the human
/// description the CLI also prints. ActorUserId is always null — refusing a bundle is
/// the system's own action, exactly like applying one.
/// </summary>
public sealed record SyncImportRefusedEvent(
    string OriginInstanceId, string BundleFileName, int? DeclaredBundleNumber, string Reason, string Detail) : IDomainEvent
{
    public Guid? ActorUserId => null;
}
