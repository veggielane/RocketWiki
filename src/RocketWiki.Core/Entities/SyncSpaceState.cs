namespace RocketWiki.Core.Entities;

/// <summary>
/// data-model.md: SyncSpaceState — high side, per-space high-water mark that makes
/// import idempotent and gap-refusing.
/// </summary>
public class SyncSpaceState
{
    public string OriginInstanceId { get; set; } = string.Empty;
    public Guid SpaceId { get; set; }
    public long AppliedSequence { get; set; }
}
