using System.ComponentModel.DataAnnotations;

namespace RocketWiki.Api.RealTime;

/// <summary>
/// Sanity caps for the co-editing relay (design.md §8 CRDT co-editing). These are
/// operational bounds, not authorization: membership (canEdit at join, §6.7) is the
/// gate; these keep a single client from ballooning hub frames or server memory.
///
/// Rate limiting is deliberately absent in v1: the Yjs provider batches keystrokes
/// and throttles awareness client-side (§8), the per-message and log caps bound the
/// damage a misbehaving client can
/// do to memory, and every member already holds canEdit — the ability to write the
/// page outright. Revisit if relay volume ever shows up in the
/// rocketwiki.coedit.relay_bytes histogram.
///
/// Every byte cap is validated at startup (Program.cs, <c>ValidateOnStart</c>): a
/// non-positive cap would drop every message of that kind — silently, since oversized
/// messages are dropped and counted rather than surfaced — so it fails the host instead.
/// The SignalR transport limit Program.cs derives from these values is computed from the
/// same validated instance.
/// </summary>
public sealed class CoEditOptions
{
    public const string SectionName = "CoEdit";

    /// <summary>Max size of one PushUpdate payload. Yjs incremental updates are
    /// typically well under a kilobyte; 512 KiB accommodates a large initial seed
    /// (a full page encoded as one update) with headroom. Oversized: dropped and
    /// counted, never relayed or logged.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "CoEdit:UpdateMaxBytes must be a positive number of bytes.")]
    public int UpdateMaxBytes { get; set; } = 512 * 1024;

    /// <summary>Max size of one awareness (caret/selection) payload. Awareness state
    /// is tiny by construction; 16 KiB is generous. Oversized: dropped and counted.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "CoEdit:AwarenessMaxBytes must be a positive number of bytes.")]
    public int AwarenessMaxBytes { get; set; } = 16 * 1024;

    /// <summary>Max size of one ReseedEditSession full-state snapshot — a whole
    /// document's encoded Y.Doc state, so larger than any incremental update.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "CoEdit:SnapshotMaxBytes must be a positive number of bytes.")]
    public int SnapshotMaxBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Total retained update-log bytes per session before the server demands a
    /// save-and-reseed (ReseedRequired → ReseedEditSession). Bounds both server
    /// memory and the replay a late joiner must absorb. Relay continues while the
    /// reseed is pending — refusing appends would silently fork members' documents,
    /// which is worse than a temporarily oversized log.
    /// </summary>
    [Range(1, long.MaxValue, ErrorMessage = "CoEdit:LogCapBytes must be a positive number of bytes.")]
    public long LogCapBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// How long an empty session (last member left/disconnected) retains its update
    /// log before it is dropped. Long enough that a browser refresh or transient
    /// disconnect re-joins the same session; short enough that abandoned sessions
    /// don't pin memory. After the drop, the next joiner seeds fresh from the page's
    /// saved CurrentContent — unsaved live edits die with the last member's local
    /// doc either way, so nothing recoverable is lost (see design report: no log
    /// persistence in v1).
    /// </summary>
    public TimeSpan EmptySessionGrace { get; set; } = TimeSpan.FromSeconds(60);
}
