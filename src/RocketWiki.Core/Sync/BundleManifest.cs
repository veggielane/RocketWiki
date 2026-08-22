using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Sync;

/// <summary>
/// design.md §12: the manifest travels alongside events.ndjson and blobs/ inside one
/// numbered bundle zip. PreviousManifestHash chains bundles together so a missing,
/// reordered, or tampered bundle is a detected error, never a silent absorb.
/// </summary>
public sealed record BundleManifest(
    string InstanceId,
    int BundleNumber,
    string? PreviousManifestHash,
    string PayloadSha256,
    IReadOnlyDictionary<string, SpaceEventRange> SpaceEventRanges);

/// <summary>SequenceNumber 0 marks a baseline (synthesized) line - never gap-checked, always applied first.</summary>
public sealed record SpaceEventRange(Guid SpaceId, long FromSequence, long ToSequence, int EventCount);

/// <summary>One line of events.ndjson, before it's written to the wire.</summary>
public sealed record BundleEventLine(
    string SpaceKey,
    Guid SpaceId,
    long SequenceNumber,
    SyncEventType EventType,
    string PayloadJson,
    DateTime CreatedAtUtc);

/// <summary>The actual wire shape of one events.ndjson line - EventType is the enum's name (a string), not its numeric value, so the file stays portable/greppable across instances.</summary>
public sealed record NdjsonEventRecord(string SpaceKey, Guid SpaceId, long SequenceNumber, string EventType, string PayloadJson, DateTime CreatedAtUtc);
