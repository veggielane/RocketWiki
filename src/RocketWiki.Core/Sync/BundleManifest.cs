using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Sync;

/// <summary>
/// The bundle file format's version story, in one place. Two rules make version skew a
/// detected error rather than a silent absorb (the same integrity philosophy as the
/// manifest hash chain):
///
/// 1. The manifest declares <c>formatVersion</c>. A format-1 manifest predates the field
///    entirely, so deserializing one yields <see cref="LegacyVersion"/> via the record
///    parameter's default — absence IS the version marker for the legacy era.
/// 2. The events entry is named per format (<see cref="EventsEntryName"/>): format 1
///    wrote <c>events.ndjson</c>; format 2 (revision history, design.md §12's "full
///    snapshot including revision history") writes <c>events.v2.ndjson</c>. That rename
///    is deliberate, not cosmetic: a format-1 importer knows nothing about
///    <c>formatVersion</c> and would otherwise absorb a format-2 bundle while silently
///    dropping its revision history — an unrecoverable, invisible gap on the high side
///    (an applied bundle is never re-applied). Instead its "bundle is missing
///    'events.ndjson'" guard fires, which its CLI already turns into a loud exit-2
///    <c>sync.import.refused</c> (reason: unreadable). Old importers refuse new bundles
///    by construction; they cannot be taught to, since they're already deployed.
///
/// The same rule carried format 3 (design.md §21.10): a marking payload — the
/// <c>marking</c> object on a PageUpsert line and the PageMarking event itself — now
/// carries <c>selectors</c>, and the events entry is <c>events.v3.ndjson</c>, so a
/// format-2 importer refuses a format-3 bundle by the same missing-entry guard rather
/// than absorbing it with every compartment silently dropped (which would land
/// compartmented content visible to the whole space — the exact widening §21.15 exists
/// to prevent).
///
/// A format-3 importer accepts EVERY earlier era: format-1 and format-2 bundles (already
/// on disk, or produced by a not-yet-upgraded low side) import exactly as before — a
/// marking without a <c>selectors</c> key carries none, documented — and anything ABOVE
/// <see cref="CurrentVersion"/> is refused with a typed <c>BundleFormatUnsupportedError</c>
/// before a single byte of events is parsed.
/// </summary>
public static class BundleFormat
{
    /// <summary>Format 1: no formatVersion field in the manifest, events under "events.ndjson", baselines carry current state only.</summary>
    public const int LegacyVersion = 1;

    /// <summary>Format 2: manifest declares formatVersion, events under "events.v2.ndjson", PageUpsert payloads carry revision history.</summary>
    public const int RevisionHistoryVersion = 2;

    /// <summary>Format 3: events under "events.v3.ndjson", every marking payload carries a <c>selectors</c> object (design.md §21.10).</summary>
    public const int CurrentVersion = 3;

    public static string EventsEntryName(int formatVersion) =>
        formatVersion >= CurrentVersion ? "events.v3.ndjson"
        : formatVersion == RevisionHistoryVersion ? "events.v2.ndjson"
        : "events.ndjson";
}

/// <summary>
/// design.md §12: the manifest travels alongside the events file and blobs/ inside one
/// numbered bundle zip. PreviousManifestHash chains bundles together so a missing,
/// reordered, or tampered bundle is a detected error, never a silent absorb.
/// FormatVersion defaults to <see cref="BundleFormat.LegacyVersion"/> so a manifest
/// written before the field existed deserializes as the format it actually is.
/// </summary>
public sealed record BundleManifest(
    string InstanceId,
    int BundleNumber,
    string? PreviousManifestHash,
    string PayloadSha256,
    IReadOnlyDictionary<string, SpaceEventRange> SpaceEventRanges,
    int FormatVersion = BundleFormat.LegacyVersion);

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
