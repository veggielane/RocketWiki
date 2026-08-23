namespace RocketWiki.Core.Services;

public sealed record ExportedBundleInfo(int BundleNumber, string BundleFilePath, int EventCount);

/// <summary>
/// design.md §12: drains the sync outbox into numbered bundle files. Not gated by
/// Principal/canEdit - export is a system/operator-triggered job (the future
/// RocketWiki.Sync CLI, not built here), not a per-user GraphQL mutation.
///
/// ExportBaselineAsync and ExportIncrementalAsync are deliberately separate calls
/// rather than one method that auto-detects "is this space new": the caller (the CLI,
/// eventually) knows when a space was just flagged exported and should invoke the
/// baseline export exactly once for it, before relying on incremental drains.
/// </summary>
public interface IBundleExportService
{
    /// <summary>
    /// Full snapshot for one space, as its own numbered bundle: every live page's
    /// current state PLUS its complete PageRevision history, each revision's author
    /// resolved to shadow-user-creatable identity (design.md §12: "full snapshot
    /// including revision history"). Written as bundle format 2 - see
    /// RocketWiki.Core.Sync.BundleFormat for the versioning contract.
    /// </summary>
    Task<ExportedBundleInfo> ExportBaselineAsync(
        Guid spaceId, string outputDirectory, string localInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Drains every not-yet-exported SyncOutboxEvent across all exported spaces into one new bundle. Null if nothing is pending.</summary>
    Task<ExportedBundleInfo?> ExportIncrementalAsync(
        string outputDirectory, string localInstanceId, CancellationToken cancellationToken = default);
}
