namespace RocketWiki.Core.Entities;

/// <summary>data-model.md: SyncImportState — high side, one row per origin instance.</summary>
public class SyncImportState
{
    public string OriginInstanceId { get; set; } = string.Empty;
    public int LastBundleNumber { get; set; }
    public string LastManifestHash { get; set; } = string.Empty;
    public DateTime LastImportAtUtc { get; set; }
}
