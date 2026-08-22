using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Entities;

/// <summary>
/// data-model.md / design.md §12: SyncOutboxEvent — low side, append-only, gap-free
/// per-space sequence maintained via Space.LastOutboxSequence.
/// </summary>
public class SyncOutboxEvent
{
    public long Id { get; set; }
    public Guid SpaceId { get; set; }
    public Space? Space { get; set; }
    public long SequenceNumber { get; set; }
    public SyncEventType EventType { get; set; }

    /// <summary>Full Markdown, not diffs; attachments referenced by ContentHash.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public int? ExportedInBundle { get; set; }
}
