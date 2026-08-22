using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

public sealed record ImportedBundleSummary(int BundleNumber, int EventsApplied, bool WasDuplicate);

/// <summary>
/// design.md §12: applies bundles strictly in order and refuses gaps; apply is
/// idempotent, so re-delivering an already-applied bundle is a harmless no-op, not an
/// error. Every import is audited as `sync.import` with the bundle id and event range -
/// AuditContext is required for the same reason it is everywhere else in this pipeline
/// (MissingAuditContextException), even though the actor is always the system, not a user.
/// Not gated by Principal - import is a system/operator-triggered job, same as export.
/// </summary>
public interface IBundleImportService
{
    Task<PageMutationResult<ImportedBundleSummary>> ImportAsync(
        string bundleFilePath, string originInstanceId, AuditContext auditContext, CancellationToken cancellationToken = default);
}
