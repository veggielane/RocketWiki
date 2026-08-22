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
    /// Full current-state snapshot for one space, as its own numbered bundle.
    /// Simplification, flagged: this snapshots each live page's CURRENT content only,
    /// not "including revision history" as design.md §12 literally specifies - full
    /// history replay was out of reach in this pass; see the accompanying report.
    /// </summary>
    Task<ExportedBundleInfo> ExportBaselineAsync(
        Guid spaceId, string outputDirectory, string localInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Drains every not-yet-exported SyncOutboxEvent across all exported spaces into one new bundle. Null if nothing is pending.</summary>
    Task<ExportedBundleInfo?> ExportIncrementalAsync(
        string outputDirectory, string localInstanceId, CancellationToken cancellationToken = default);
}
