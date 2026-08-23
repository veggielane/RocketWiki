using System.Diagnostics;
using System.Diagnostics.Metrics;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Data.Telemetry;

/// <summary>
/// Operational instrumentation for the persistence layer (design.md §15). Same rules as
/// <see cref="Core.Telemetry.CoreTelemetry"/>: plain <see cref="System.Diagnostics"/>
/// primitives, no OpenTelemetry package dependency, and no page content, search text, or
/// principal attribute values in any tag.
///
/// Spans here are deliberately sparse. Every individual query is already a client span
/// from <c>AddSqlClientInstrumentation</c> (registered by Aspire's
/// <c>AddSqlServerDbContext</c>), so a span per service method would mostly restate what
/// SQL Client already reports. What SQL Client cannot show is a *unit of work that spans
/// several queries* — a subtree delete that loads a page, its descendants, the space's
/// grants and every relevant restriction before one SaveChanges. Those get a span; simple
/// single-query reads do not.
/// </summary>
public static class DataTelemetry
{
    public const string SourceName = "RocketWiki.Data";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    /// <summary>
    /// design.md §12: entries appended to the sync outbox journal, by event type. The
    /// low-side signal that content is actually queueing for the next bundle — a flat
    /// line here while pages are being edited means the outbox writer is silently
    /// skipping (an unexported space, an unclassified event).
    /// </summary>
    public static readonly Counter<long> OutboxEntriesAppended =
        Meter.CreateCounter<long>("rocketwiki.sync.outbox_entries_appended", "{entry}",
            "Sync outbox entries appended, by sync event type.");

    public const string PageIdTag = "rocketwiki.page.id";
    public const string PageCountTag = "rocketwiki.page.count";
    public const string SpaceIdTag = "rocketwiki.space.id";
    public const string SpaceKeyTag = "rocketwiki.space.key";
    public const string RevisionNumberTag = "rocketwiki.page.revision_number";
    public const string SyncEventTypeTag = "rocketwiki.sync.event_type";
    public const string BundleNumberTag = "rocketwiki.sync.bundle_number";
    public const string BundleEntryCountTag = "rocketwiki.sync.entry_count";
    public const string BundleDuplicateTag = "rocketwiki.sync.was_duplicate";
    public const string OutcomeTag = "rocketwiki.outcome";

    public const string SubtreeDeleteSpan = "rocketwiki.page.delete_subtree";
    public const string SubtreeRestoreSpan = "rocketwiki.page.restore_subtree";
    public const string RestoreRevisionSpan = "rocketwiki.page.restore_revision";
    public const string MovePageSpan = "rocketwiki.page.move";
    public const string BundleExportBaselineSpan = "rocketwiki.sync.bundle_export_baseline";
    public const string BundleExportIncrementalSpan = "rocketwiki.sync.bundle_export_incremental";
    public const string BundleImportSpan = "rocketwiki.sync.bundle_import";

    /// <summary>
    /// Starts an internal span, or returns null when nothing is listening — the standard
    /// <see cref="ActivitySource"/> contract, and why every call site null-conditionals
    /// its <c>SetTag</c> calls rather than paying for tag construction unconditionally.
    /// </summary>
    public static Activity? StartSpan(string name) =>
        ActivitySource.StartActivity(name, ActivityKind.Internal);

    public static void RecordOutboxEntryAppended(SyncEventType eventType) =>
        OutboxEntriesAppended.Add(1, new KeyValuePair<string, object?>(SyncEventTypeTag, eventType.ToString()));

    /// <summary>
    /// Marks a span with the mutation's outcome. The tag is the failure's *type name*
    /// (<c>ReadOnlyReplicaError</c>, <c>SubtreeOperationForbiddenError</c>) — never the
    /// error's message, which can quote a title, and never the failing rule, which the
    /// audit log already records properly (§7).
    /// </summary>
    public static void SetOutcome(Activity? activity, string outcome)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(OutcomeTag, outcome);
        activity.SetStatus(outcome == SuccessOutcome ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }

    public const string SuccessOutcome = "success";

    /// <summary>
    /// Closes out a mutation span from its result. Records the failure's <b>type name</b>
    /// and nothing else — <see cref="StaleRevisionError"/> carries
    /// <c>LatestTitle</c>/<c>LatestContent</c>, so tagging the error object, its message,
    /// or any of its members would put page content straight into a trace, which
    /// design.md §15 forbids outright.
    /// </summary>
    public static PageMutationResult<T> Finish<T>(Activity? activity, PageMutationResult<T> result)
    {
        SetOutcome(activity, result.IsSuccess ? SuccessOutcome : result.Error.GetType().Name);
        return result;
    }
}
